using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ocelot.Administration;
using Ocelot.DependencyInjection;
using System.Reflection;

namespace Ocelot.UnitTests.Administration;

public class OcelotAdministrationBuilderTests : UnitTest
{
    private readonly IServiceCollection _services;
    private IServiceProvider _serviceProvider;
    private readonly IConfiguration _configRoot;
    private IOcelotBuilder _ocelotBuilder;
    private Exception _ex;

    public OcelotAdministrationBuilderTests()
    {
        _configRoot = new ConfigurationRoot(new List<IConfigurationProvider>());
        _services = new ServiceCollection();
        _services.AddSingleton(GetHostingEnvironment());
        _services.AddSingleton(_configRoot);
    }

    private static IWebHostEnvironment GetHostingEnvironment()
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment
            .Setup(e => e.ApplicationName)
            .Returns(typeof(OcelotAdministrationBuilderTests).GetTypeInfo().Assembly.GetName().Name);

        return environment.Object;
    }

    //keep
    [Fact]
    public void should_set_up_administration_with_identity_server_options()
    {
        Action<JwtBearerOptions> options = o => { };

        this.Given(x => WhenISetUpOcelotServices())
            .When(x => WhenISetUpAdministration(options))
            .Then(x => ThenAnExceptionIsntThrown())
            .Then(x => ThenTheCorrectAdminPathIsRegitered())
            .BDDfy();
    }

    //keep
    [Fact]
    public void should_set_up_administration()
    {
        this.Given(x => WhenISetUpOcelotServices())
            .When(x => WhenISetUpAdministration())
            .Then(x => ThenAnExceptionIsntThrown())
            .Then(x => ThenTheCorrectAdminPathIsRegitered())
            .BDDfy();
    }

    private void WhenISetUpAdministration()
    {
        _ocelotBuilder.AddAdministration("/administration", "secret");
    }

    private void WhenISetUpAdministration(Action<JwtBearerOptions> options)
    {
        _ocelotBuilder.AddAdministration("/administration", options);
    }

    private void ThenTheCorrectAdminPathIsRegitered()
    {
        _serviceProvider = _services.BuildServiceProvider(true);
        var path = _serviceProvider.GetService<IAdministrationPath>();
        path.Path.ShouldBe("/administration");
    }

    private void WhenISetUpOcelotServices()
    {
        try
        {
            _ocelotBuilder = _services.AddOcelot(_configRoot);
        }
        catch (Exception e)
        {
            _ex = e;
        }
    }

    private void ThenAnExceptionIsntThrown()
    {
        _ex.ShouldBeNull();
    }
}
