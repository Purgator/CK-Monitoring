using CK.AspNet.Tester;
using CK.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using System.Linq;
using System.Threading.Tasks;

namespace CK.Monitoring.Hosting.Tests
{
    [TestFixture]
    public partial class HostingTests
    {

        [Test]
        public void IActivityMonitor_and_ActivityMonitor_resolve_to_the_same_object()
        {
            var host = new HostBuilder()
                        .UseCKMonitoring()
                        .Build();

            var ia = host.Services.GetRequiredService<IActivityMonitor>();
            var a = host.Services.GetRequiredService<ActivityMonitor>();
            ia.Should().BeSameAs( a );
        }

        [Test]
        public void No_CKMonitoring_section_and_existing_Monitoring_section_throws()
        {
            var config = new DynamicConfigurationSource();
            config["Monitoring"] = "true";
            FluentActions.Invoking( () =>
            {
                var host = new HostBuilder()
                        .ConfigureAppConfiguration( ( hostingContext, c ) => c.Add( config ) )
                        .UseCKMonitoring()
                        .Build();
            } ).Should().Throw<CKException>();
        }

        [Test]
        public void GrandOutput_MinimalFilter_configuration_works()
        {
            DemoSinkHandler.Reset();
            var config = new DynamicConfigurationSource();
            config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";

            var host = new HostBuilder()
                        .ConfigureAppConfiguration( ( hostingContext, c ) => c.Add( config ) )
                        .UseCKMonitoring()
                        .Build();

            var m = new ActivityMonitor();
            m.ActualFilter.Should().Be( LogFilter.Undefined, "Initially Undefined." );

            config["CK-Monitoring:GrandOutput:MinimalFilter"] = "Debug";

            System.Threading.Thread.Sleep( 200 );
            m.ActualFilter.Should().Be( LogFilter.Debug, "First Debug applied." );

            config["CK-Monitoring:GrandOutput:MinimalFilter"] = "{Off,Debug}";
            System.Threading.Thread.Sleep( 200 );
            m.ActualFilter.Should().Be( new LogFilter( LogLevelFilter.Off, LogLevelFilter.Debug ), "Explicit {Off,Debug} filter." );

            config["CK-Monitoring:GrandOutput:MinimalFilter"] = null;
            System.Threading.Thread.Sleep( 200 );
            m.ActualFilter.Should().Be( new LogFilter( LogLevelFilter.Off, LogLevelFilter.Debug ), "Null doesn't change anything." );

            // Restores the Debug level (we are on the GrandOutput.Default).
            config["CK-Monitoring:GrandOutput:MinimalFilter"] = "Debug";
            System.Threading.Thread.Sleep( 200 );

            var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).ToArray();
            texts.Where( e => e != null && e.StartsWith( "GrandOutput.Default configuration n°4." ) ).Should().NotBeEmpty();
            texts.Where( e => e != null && e.StartsWith( "GrandOutput.Default configuration n°5." ) )
                .Should().BeEmpty( "There has been the initial configuration (n°0) and 4 reconfigurations." );
        }

        [Test]
        public async Task Invalid_configurations_are_skipped_and_errors_go_to_the_current_handlers_Async()
        {
            DemoSinkHandler.Reset();
            var config = new DynamicConfigurationSource();
            config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";
            var host = new HostBuilder()
                        .ConfigureAppConfiguration( ( hostingContext, c ) => c.Add( config ) )
                        .UseCKMonitoring()
                        .Build();
            await host.StartAsync();

            var m = new ActivityMonitor( "The topic!" );

            m.Info( "BEFORE" );
            config["CK-Monitoring:GrandOutput:Handlers:Invalid Handler"] = "true";
            m.Info( "AFTER" );

            await host.StopAsync();

            DemoSinkHandler.LogEvents.Select( e => e.Text ).Should()
                   .Contain( "Topic: The topic!" )
                   .And.Contain( "BEFORE" )
                   .And.Contain( "While applying dynamic configuration." )
                   .And.Contain( "AFTER" );
        }


        [Test]
        public async Task Configuration_changes_dont_stutter_Async()
        {
            DemoSinkHandler.Reset();
            var config = new DynamicConfigurationSource();
            config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";
            var host = new HostBuilder()
                        .ConfigureAppConfiguration( ( hostingContext, c ) => c.Add( config ) )
                        .UseCKMonitoring()
                        .Build();
            await host.StartAsync();

            var m = new ActivityMonitor( "The starting topic!" );

            config["CK-Monitoring:GrandOutput:Handlers:Console"] = "true";

            await Task.Delay( 200 );

            m.Info( "DONE!" );

            await host.StopAsync();

            var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).Concatenate( System.Environment.NewLine );
            texts.Should()
                   .Contain( "GrandOutput.Default configuration n°0" )
                   .And.Contain( "GrandOutput.Default configuration n°1" )
                   .And.NotContain( "GrandOutput.Default configuration n°2" )
                   .And.Contain( "DONE!" );
        }

        [Test]
        public async Task TagFilters_works_Async()
        {
            CKTrait Sql = ActivityMonitor.Tags.Register( "Sql" );
            CKTrait Machine = ActivityMonitor.Tags.Register( "Machine" );

            DemoSinkHandler.Reset();
            var config = new DynamicConfigurationSource();
            config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";
            config["CK-Monitoring:GrandOutput:MinimalFilter"] = "Trace";
            config["CK-Monitoring:TagFilters:0:0"] = "Sql";
            config["CK-Monitoring:TagFilters:0:1"] = "Debug";
            config["CK-Monitoring:TagFilters:1:0"] = "Machine";
            config["CK-Monitoring:TagFilters:1:1"] = "Release!";

            var host = new HostBuilder()
                        .ConfigureAppConfiguration( ( hostingContext, c ) => c.Add( config ) )
                        .UseCKMonitoring()
                        .Build();
            await host.StartAsync();

            var m = new ActivityMonitor();

            RunWithTagFilters( Sql, Machine, m );

            // Removing the TagFilters totally should keep the current filters.
            using( config.StartBatch() )
            {
                config.Remove( "CK-Monitoring:TagFilters:0:0" );
                config.Remove( "CK-Monitoring:TagFilters:0:1" );
                config.Remove( "CK-Monitoring:TagFilters:1:0" );
                config.Remove( "CK-Monitoring:TagFilters:1:1" );
            }

            await Task.Delay( 200 );

            RunWithTagFilters( Sql, Machine, m );

            config["CK-Monitoring:TagFilters:0"] = "";

            await Task.Delay( 200 );

            m.Debug( Sql, "NOP! This is in Debug!" );
            m.Trace( Machine, "SHOW!" );
            m.Trace( Machine | Sql, "Yes again!" );
            m.Trace( "DONE!" );

            await Task.Delay( 200 );

            var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).Concatenate( System.Environment.NewLine );
            texts.Should()
                   .Contain( "SHOW!" )
                   .And.Contain( "Yes again!" )
                   .And.NotContain( "NOP! This is in Debug!" )
                   .And.Contain( "DONE!" );

            await host.StopAsync();

            static void RunWithTagFilters( CKTrait Sql, CKTrait Machine, ActivityMonitor m )
            {
                m.Debug( Sql, "YES: Sql!" );
                m.Trace( Machine, "NOSHOW" );
                m.Trace( Machine | Sql, "Yes again!" );
                m.Trace( "DONE!" );

                System.Threading.Thread.Sleep( 200 );

                var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).Concatenate( System.Environment.NewLine );
                texts.Should()
                       .Contain( "YES: Sql!" )
                       .And.Contain( "Yes again!" )
                       .And.NotContain( "NOSHOW" )
                       .And.Contain( "DONE!" );

                DemoSinkHandler.Reset();
            }
        }

    }
}
