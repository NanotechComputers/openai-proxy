using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

builder.Services.AddOpenApi();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.MapDefaultEndpoints();

// Log incoming IDE requests
app.Use(async (ctx, next) =>
{
    var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    app.Logger.LogInformation("Incoming {Method} {Path} from {Remote}", ctx.Request.Method, ctx.Request.Path, remote);
    await next();
});

// These should be registered before the POST proxy endpoints so the IDE's GET probes get matched.
var config = app.Configuration.GetSection("Azure");
var azureEndpointFull = config["EndpointFull"];
var azureBase = config["Base"];
var azureDeployment = config["Deployment"];
var azureApiVersion = config["ApiVersion"] ?? "2025-01-01-preview";
var azureKey = config["Key"];

string[] knownModels = new[] { azureDeployment ?? "gpt-5-chat" };

app.MapGet("/v1/chat/completions/models", () =>
{
    var result = new { data = knownModels.Select(m => new { id = m, @object = "model" }) };
    return Results.Json(result);
});

app.MapGet("/v1/responses/models", () =>
{
    var result = new { data = knownModels.Select(m => new { id = m, @object = "model" }) };
    return Results.Json(result);
});

app.MapGet("/v1/models", () =>
{
    var result = new { data = knownModels.Select(m => new { id = m, @object = "model" }) };
    return Results.Json(result);
});

// Some clients also probe /models or /api/v0/models - return a simplified array
app.MapGet("/models", () => Results.Json(new { models = knownModels }));
app.MapGet("/api/v0/models", () => Results.Json(new { models = knownModels }));

// Respond to OPTIONS preflight where clients probe (harmless)
app.MapMethods("/v1/{**any}", new[] { "OPTIONS" }, () => Results.Ok());
app.MapMethods("/models", new[] { "OPTIONS" }, () => Results.Ok());
app.MapMethods("/api/v0/models", new[] { "OPTIONS" }, () => Results.Ok());


string BuildAzureUrl(HttpRequest incoming)
{
    if (!string.IsNullOrWhiteSpace(azureEndpointFull))
        return azureEndpointFull!;

    if (string.IsNullOrWhiteSpace(azureBase) || string.IsNullOrWhiteSpace(azureDeployment))
        throw new InvalidOperationException("Azure Base or Deployment not configured in appsettings.json.");

    // Map common OpenAI-style paths to Azure-style paths.
    var incomingPath = incoming.Path.ToString().TrimStart('/');
    string azurePath;
    if (incomingPath.StartsWith("v1/chat/completions", StringComparison.OrdinalIgnoreCase))
        azurePath = $"openai/deployments/{azureDeployment}/chat/completions";
    else if (incomingPath.StartsWith("v1/responses", StringComparison.OrdinalIgnoreCase))
        azurePath = $"openai/deployments/{azureDeployment}/responses";
    else
    {
        var afterV1 = incomingPath.StartsWith("v1/") ? incomingPath.Substring(3) : incomingPath;
        azurePath = $"openai/deployments/{azureDeployment}/{afterV1}";
    }

    var qs = incoming.QueryString.HasValue
        ? incoming.QueryString.Value!.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
            ? incoming.QueryString.Value
            : $"{incoming.QueryString.Value}&api-version={azureApiVersion}"
        : $"?api-version={azureApiVersion}";

    return $"{azureBase!.TrimEnd('/')}/{azurePath}{qs}";
}

var httpClient = new HttpClient()
{
    Timeout = TimeSpan.FromMinutes(10)
};

async Task ForwardRequest(HttpRequest req, HttpResponse resp)
{
    if (string.IsNullOrWhiteSpace(azureKey))
    {
        resp.StatusCode = StatusCodes.Status500InternalServerError;
        await resp.WriteAsJsonAsync(new { error = "azure_key_missing", details = "Azure key not configured in appsettings.json (Azure:Key)." });
        return;
    }

    string azureUrl;
    try
    {
        azureUrl = BuildAzureUrl(req);
    }
    catch (Exception ex)
    {
        resp.StatusCode = StatusCodes.Status500InternalServerError;
        await resp.WriteAsJsonAsync(new { error = "azure_url_build_failed", details = ex.Message });
        return;
    }

    using var message = new HttpRequestMessage(HttpMethod.Post, azureUrl);

    // Copy request body stream for forwarding (supports large/streaming bodies)
    if (req.ContentLength > 0 || req.Body.CanRead)
    {
        message.Content = new StreamContent(req.Body);
        if (!string.IsNullOrEmpty(req.ContentType))
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(req.ContentType);
    }

    // Azure requires the 'api-key' header
    message.Headers.Remove("api-key");
    message.Headers.TryAddWithoutValidation("api-key", azureKey);

    if (req.Headers.TryGetValue("Accept", out var accept))
        message.Headers.TryAddWithoutValidation("Accept", (string?)accept);

    HttpResponseMessage azureResponse;
    try
    {
        azureResponse = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, req.HttpContext.RequestAborted);
    }
    catch (OperationCanceledException) when (req.HttpContext.RequestAborted.IsCancellationRequested)
    {
        // client cancelled
        app.Logger.LogInformation("Request cancelled by caller.");
        resp.StatusCode = StatusCodes.Status499ClientClosedRequest;
        return;
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error forwarding request to Azure.");
        resp.StatusCode = StatusCodes.Status502BadGateway;
        await resp.WriteAsJsonAsync(new { error = "forward_error", details = ex.Message });
        return;
    }

    // Copy status code
    resp.StatusCode = (int)azureResponse.StatusCode;

    // Copy headers (limited set as ASP.NET may restrict some)
    foreach (var header in azureResponse.Headers)
        resp.Headers[header.Key] = header.Value.ToArray();
    if (azureResponse.Content != null)
    {
        foreach (var header in azureResponse.Content.Headers)
            resp.Headers[header.Key] = header.Value.ToArray();
    }

    // Remove transfer-encoding if present (Kestrel manages this)
    resp.Headers.Remove("transfer-encoding");

    // Stream response body back to caller
    if (azureResponse.Content != null)
    {
        using var responseStream = await azureResponse.Content.ReadAsStreamAsync(req.HttpContext.RequestAborted);
        await responseStream.CopyToAsync(resp.Body, 81920, req.HttpContext.RequestAborted);
    }
}


void MapProxyPost(string pattern)
{
    app.MapPost(pattern, async ctx =>
    {
        try
        {
            await ForwardRequest(ctx.Request, ctx.Response);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "proxy_error", details = ex.Message });
        }
    });
}

MapProxyPost("/v1/chat/completions");
MapProxyPost("/v1/responses");

// Catch all for /v1/*
MapProxyPost("/v1/{**rest}");

app.MapOpenApi();

app.Run();