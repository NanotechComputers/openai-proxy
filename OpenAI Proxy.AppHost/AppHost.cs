using Scalar.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

var apiService = builder.AddProject<Projects.OpenAI_Proxy_ApiService>("apiservice")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// Add Scalar API Reference
var scalar = builder.AddScalarApiReference(options =>
{
    options
        .AddDocument("v1", "OpenAI Proxy API v1");
    options
        .PreferHttpsEndpoint()
        .AllowSelfSignedCertificates();

    // UI customization
    options.Layout = ScalarLayout.Classic;
    options.Theme = ScalarTheme.Kepler;
    options.HideDarkModeToggle = true;
    options.ShowSidebar = false;
    
});
// Register services with the API Reference
scalar
    .WithApiReference(apiService);

builder.Build().Run();