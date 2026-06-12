using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SupportPoc.Shared.Auth;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class ProductionSecurityGuardTests : IDisposable
{
    private readonly string? _originalBypass;

    public ProductionSecurityGuardTests()
    {
        _originalBypass = Environment.GetEnvironmentVariable(ProductionSecurityGuard.AllowInsecureEnvVar);
        Environment.SetEnvironmentVariable(ProductionSecurityGuard.AllowInsecureEnvVar, null);
    }

    [Fact]
    public void Validate_throws_in_production_when_entra_disabled()
    {
        var env = new StubHostEnvironment(Environments.Production);
        var config = Config(entraEnabled: false);

        var ex = Assert.Throws<InvalidOperationException>(() => ProductionSecurityGuard.Validate(env, config));
        Assert.Contains("AzureAd:Enabled=true", ex.Message);
        Assert.Contains(ProductionSecurityGuard.AllowInsecureEnvVar, ex.Message);
    }

    [Fact]
    public void Validate_allows_development_without_entra()
    {
        var env = new StubHostEnvironment(Environments.Development);
        var config = Config(entraEnabled: false);

        ProductionSecurityGuard.Validate(env, config);
    }

    [Fact]
    public void Validate_allows_production_with_entra_enabled()
    {
        var env = new StubHostEnvironment(Environments.Production);
        var config = Config(entraEnabled: true);

        ProductionSecurityGuard.Validate(env, config);
    }

    [Fact]
    public void Validate_allows_production_with_insecure_bypass_env()
    {
        Environment.SetEnvironmentVariable(ProductionSecurityGuard.AllowInsecureEnvVar, "true");
        var env = new StubHostEnvironment(Environments.Production);
        var config = Config(entraEnabled: false);

        ProductionSecurityGuard.Validate(env, config);
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ProductionSecurityGuard.AllowInsecureEnvVar, _originalBypass);

    private static IConfiguration Config(bool entraEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:Enabled"] = entraEnabled ? "true" : "false"
            })
            .Build();

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
