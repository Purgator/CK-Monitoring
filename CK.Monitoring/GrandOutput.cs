using System;
using System.Collections.Generic;
using System.Threading;
using CK.Core;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace CK.Monitoring
{
    /// <summary>
    /// A GrandOutput collects activity of multiple <see cref="IActivityMonitor"/>. 
    /// It is usually useless to explicitly create an instance of GrandOutput: the <see cref="Default"/> one is 
    /// available as soon as <see cref="EnsureActiveDefault"/> is called 
    /// and will be automatically used by new <see cref="ActivityMonitor"/>.
    /// </summary>
    public sealed partial class GrandOutput : IDisposable
    {
        readonly List<WeakReference<GrandOutputClient>> _clients;
        readonly DispatcherSink _sink;
        readonly IdentityCard _identityCard;
        LogFilter _minimalFilter;

        static GrandOutput? _default;
        static MonitorTraceListener? _traceListener;
        static readonly object _defaultLock = new object();

        /// <summary>
        /// The tag that marks all external log entry sent by <see cref="AppDomain.UnhandledException"/>
        /// and <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>.
        /// </summary>
        public static readonly CKTrait UnhandledException = ActivityMonitor.Tags.Register( "UnhandledException" );

        /// <summary>
        /// The name of the fake monitor for external logs.
        /// </summary>
        public const string ExternalLogMonitorUniqueId = "§ext";

        /// <summary>
        /// The default grand output identifier.
        /// </summary>
        public const string UnknownGrandOutputId = "§none";

        /// <summary>
        /// Gets the default <see cref="GrandOutput"/> for the current Application Domain.
        /// Note that <see cref="EnsureActiveDefault"/> must have been called, otherwise this static property is null
        /// and that this Default can be <see cref="Dispose()"/> at any time (this static property will be set back to null).
        /// </summary>
        public static GrandOutput? Default => _default;

        /// <summary>
        /// Ensures that the <see cref="Default"/> GrandOutput is created and that any <see cref="ActivityMonitor"/> that will be created in this
        /// application domain will automatically have a <see cref="GrandOutputClient"/> registered for this Default GrandOutput.
        /// If the Default is already initialized, the <paramref name="configuration"/> is applied.
        /// </summary>
        /// <param name="configuration">
        /// Configuration to apply to the default GrandOutput.
        /// When null, a default configuration with a <see cref="Handlers.TextFileConfiguration"/> in a "Text" path is configured.
        /// </param>
        /// <param name="clearExistingTraceListeners">
        /// If the <see cref="Default"/> is actually instantiated, existing <see cref="Trace.Listeners"/>
        /// are cleared before registering a <see cref="MonitorTraceListener"/> associated to this default grand output.
        /// See remarks.
        /// </param>
        /// <returns>The Default GrandOutput that has been created or reconfigured.</returns>
        /// <remarks>
        /// <para>
        /// This method is thread-safe (a simple lock protects it) and uses a <see cref="ActivityMonitor.AutoConfiguration"/> action 
        /// that uses <see cref="EnsureGrandOutputClient(IActivityMonitor)"/> on newly created ActivityMonitor.
        /// </para>
        /// <para>
        /// The Default GrandOutput also adds a <see cref="MonitorTraceListener"/> in the <see cref="Trace.Listeners"/> collection that
        /// has <see cref="MonitorTraceListener.FailFast"/> sets to false: <see cref="MonitoringFailFastException"/> are thrown instead of
        /// calling <see cref="Environment.FailFast(string)"/>.
        /// If this behavior must be changed, please exploit the <see cref="Trace.Listeners"/> that is a <see cref="TraceListenerCollection"/>, wide open
        /// to any modifications, and the fact that <see cref="MonitorTraceListener"/> exposes its associated grand output and
        /// that <see cref="MonitorTraceListener.FailFast"/> property can be changed at any time.
        /// </para>
        /// <para>
        /// The GrandOutput.Default can safely be <see cref="Dispose()"/> at any time: disposing the Default 
        /// sets it to null.
        /// </para>
        /// </remarks>
        static public GrandOutput EnsureActiveDefault( GrandOutputConfiguration? configuration = null, bool clearExistingTraceListeners = true )
        {
            lock( _defaultLock )
            {
                if( _default == null )
                {
                    if( configuration == null )
                    {
                        configuration = new GrandOutputConfiguration()
                                            .AddHandler( new Handlers.TextFileConfiguration() { Path = "Text" } );
                        configuration.InternalClone = true;
                    }
                    _default = new GrandOutput( true, configuration );
                    ActivityMonitor.AutoConfiguration += AutoRegisterDefault;
                    _traceListener = new MonitorTraceListener( _default, failFast: false );
                    if( clearExistingTraceListeners ) Trace.Listeners.Clear();
                    Trace.Listeners.Add( _traceListener );
                }
                else if( configuration != null ) _default.ApplyConfiguration( configuration, true );
            }
            return _default;
        }

        static void AutoRegisterDefault( IActivityMonitor m )
        {
            Default?.EnsureGrandOutputClient( m );
        }

        /// <summary>
        /// Applies a configuration.
        /// This is thread safe and can be called at any moment.
        /// </summary>
        /// <param name="configuration">The configuration to apply.</param>
        /// <param name="waitForApplication">
        /// True to block until this configuration has been applied.
        /// Note that another (new) configuration may have already replaced the given configuration
        /// once this call ends.
        /// </param>
        public void ApplyConfiguration( GrandOutputConfiguration configuration, bool waitForApplication = false )
        {
            Throw.CheckNotNullArgument( configuration );
            if( !configuration.InternalClone )
            {
                configuration = configuration.Clone();
                configuration.InternalClone = true;
            }
            _sink.ApplyConfiguration( configuration, waitForApplication );
        }

        /// <summary>
        /// Settable factory method for <see cref="IGrandOutputHandler"/>.
        /// Default implementation relies on Handlers that must be in the same 
        /// assembly and namespace as their configuration objects and named the 
        /// same without the "Configuration" suffix.
        /// </summary>
        public static Func<IHandlerConfiguration, IServiceProvider, IGrandOutputHandler> CreateHandler { get; set; } = (config,sp) =>
            {
                var t = config.GetType();
                var name = t.FullName;
                Debug.Assert( name != null && t.AssemblyQualifiedName != null );
                if( !name.EndsWith( "Configuration" ) ) Throw.Exception( $"Configuration handler type name must end with 'Configuration': {name}." );
                name = t.AssemblyQualifiedName.Replace( "Configuration,", "," );
                t = Type.GetType( name, throwOnError: true );
                Debug.Assert( t != null );
                return (IGrandOutputHandler)ActivatorUtilities.CreateInstance( sp, t, config );
            };

        static GrandOutput()
        {
            AppDomain.CurrentDomain.DomainUnload += ( o, e ) => Default?.Dispose();
            AppDomain.CurrentDomain.ProcessExit += ( o, e ) => Default?.Dispose();
        }

        /// <summary>
        /// Initializes a new <see cref="GrandOutput"/>. 
        /// </summary>
        /// <param name="config">The configuration.</param>
        public GrandOutput( GrandOutputConfiguration config )
            : this( false, config )
        {
        }

        GrandOutput( bool isDefault, GrandOutputConfiguration config )
        {
            if( config == null ) throw new ArgumentNullException( nameof( config ) );
            // Creates the identity card and the client list first.
            _identityCard = new IdentityCard();
            _clients = new List<WeakReference<GrandOutputClient>>();
            _minimalFilter = LogFilter.Undefined;
            // Starts the pump thread. Its monitor will be registered
            // in this GrandOutput.
            _sink = new DispatcherSink( m => DoEnsureGrandOutputClient( m ),
                                        _identityCard,
                                        config.TimerDuration ?? TimeSpan.FromMilliseconds(500),
                                        TimeSpan.FromMinutes( 5 ),
                                        DoGarbageDeadClients,
                                        OnFiltersChanged,
                                        isDefault );
            ApplyConfiguration( config, waitForApplication: true );
            ActivityMonitor.ExternalLog.OnExternalLog += OnExternalLog;
        }

        /// <summary>
        /// Ensures that a client for this GrandOutput is registered on a monitor.
        /// There is no need to call this method for the <see cref="Default"/> GrandOutput since
        /// clients are automatically registered for newly created <see cref="ActivityMonitor"/> (thanks
        /// to a <see cref="ActivityMonitor.AutoConfiguration"/> hook).
        /// </summary>
        /// <param name="monitor">The monitor onto which a <see cref="GrandOutputClient"/> must be registered.</param>
        /// <returns>A newly created client or the already existing one.</returns>
        public GrandOutputClient EnsureGrandOutputClient( IActivityMonitor monitor )
        {
            if( IsDisposed ) throw new ObjectDisposedException( nameof( GrandOutput ) );
            if( monitor == null ) throw new ArgumentNullException( nameof( monitor ) );
            var c = DoEnsureGrandOutputClient( monitor );
            if( c == null ) throw new ObjectDisposedException( nameof( GrandOutput ) );
            return c;
        }

        GrandOutputClient? DoEnsureGrandOutputClient( IActivityMonitor monitor )
        {
            GrandOutputClient? Register()
            {
                var c = new GrandOutputClient( this );
                lock( _clients )
                {
                    if( IsDisposed ) c = null;
                    else _clients.Add( new WeakReference<GrandOutputClient>( c ) );
                }
                return c;
            }
            return monitor.Output.RegisterUniqueClient( b => { Debug.Assert( b != null ); return b.Central == this; }, Register );
        }

        /// <summary>
        /// Gets the identity card of this GrandOutput.
        /// </summary>
        public IdentityCard IdentityCard => _identityCard;

        /// <summary>
        /// Gets this GrandOutput identifier: this is the identifier of the dispatcher sink monitor.
        /// </summary>
        public string GrandOutpuId => _sink.SinkMonitorId;

        /// <summary>
        /// Gets or directly sets the filter level for ExternalLog methods (without using the <see cref="GrandOutputConfiguration.ExternalLogLevelFilter"/> configuration).
        /// Defaults to <see cref="LogLevelFilter.None"/> (<see cref="ActivityMonitor.DefaultFilter"/>.<see cref="LogFilter.Line">Line</see>
        /// is used).
        /// Note that <see cref="ApplyConfiguration(GrandOutputConfiguration, bool)"/> changes this property.
        /// </summary>
        public LogLevelFilter ExternalLogLevelFilter { get; set; }

        /// <summary>
        /// Gets or directly sets the minimal filter (without using the <see cref="GrandOutputConfiguration.MinimalFilter"/> configuration).
        /// This minimal filter impacts all the <see cref="IActivityMonitor"/> that are bound to the <see cref="GrandOutput"/>
        /// (through the <see cref="GrandOutputClient"/>).
        /// Default to <see cref="LogFilter.Undefined"/>: there is no impact on each <see cref="IActivityMonitor.ActualFilter"/>.
        /// Note that <see cref="ApplyConfiguration(GrandOutputConfiguration, bool)"/> changes this property.
        /// </summary>
        public LogFilter MinimalFilter
        {
            get => _minimalFilter;
            set 
            {
                var m = _minimalFilter;
                if( m != value )
                {
                    _minimalFilter = value;
                    SignalClients();
                }
            }
        }

        void OnFiltersChanged( LogFilter? minimalFilter, LogLevelFilter? externalLogFilter )
        {
            if( minimalFilter.HasValue ) MinimalFilter = minimalFilter.Value;
            if( externalLogFilter.HasValue ) ExternalLogLevelFilter = externalLogFilter.Value;
        }

        /// <summary>
        /// Gets whether a log level should be emitted.
        /// We consider that as long has the log level has <see cref="CK.Core.LogLevel.IsFiltered">IsFiltered</see>
        /// bit set, the decision has already been taken and we return true.
        /// But for logs that do not claim to have been filtered, we challenge the <see cref="ExternalLogLevelFilter"/>
        /// (and if it is <see cref="LogLevelFilter.None"/>, the static <see cref="ActivityMonitor.DefaultFilter"/>).
        /// </summary>
        /// <param name="level">Log level to test.</param>
        /// <returns>True if this level should be logged otherwise false.</returns>
        public bool IsExternalLogEnabled( LogLevel level )
        {
            if( (level & LogLevel.IsFiltered) != 0 ) return true;
            LogLevelFilter filter = ExternalLogLevelFilter;
            if( filter == LogLevelFilter.None ) filter = ActivityMonitor.DefaultFilter.Line;
            return (int)filter <= (int)(level & LogLevel.Mask);
        }

        void OnExternalLog( ref ActivityMonitorLogData data )
        {
            if( IsExternalLogEnabled( data.Level ) )
            {
                _sink.ExternalLog( ref data );
            }
        }

        /// <summary>
        /// Logs an entry from any contextless source to this GrandOutput only (as opposed to using <see cref="ActivityMonitor.ExternalLog"/>
        /// that will be handled by all existing GrandOutput instances).
        /// </summary>
        /// <remarks>
        /// We consider that as long has the log level has <see cref="CK.Core.LogLevel.IsFiltered">IsFiltered</see> bit
        /// set, the decision has already been taken and here we do our job: dispatching the log.
        /// But for logs that do not claim to have been filtered, we challenge the <see cref="ExternalLogLevelFilter"/>.
        /// </remarks>
        /// <param name="level">Log level.</param>
        /// <param name="tags">Optional tags (that must belong to <see cref="ActivityMonitor.Tags.Context"/>).</param>
        /// <param name="message">String message.</param>
        /// <param name="ex">Optional exception.</param>
        public void ExternalLog( LogLevel level, CKTrait? tags, string message, Exception? ex = null )
        {
            if( (level & LogLevel.IsFiltered) == 0 )
            {
                LogLevelFilter filter = ExternalLogLevelFilter;
                if( filter == LogLevelFilter.None ) filter = ActivityMonitor.DefaultFilter.Line;
                if( (int)filter > (int)(level & LogLevel.Mask) ) return;
            }
            _sink.ExternalLog( level, tags, message, ex );
        }

        /// <summary>
        /// Logs an entry from any contextless source to this GrandOutput only (as opposed to using <see cref="ActivityMonitor.ExternalLog"/>
        /// that will be handled by all existing GrandOutput instances).
        /// </summary>
        /// <remarks>
        /// We consider that as long has the log level has <see cref="CK.Core.LogLevel.IsFiltered">IsFiltered</see> bit
        /// set, the decision has already being taken and here we do our job: dispatching of the log.
        /// But for logs that do not claim to have been filtered, we challenge the <see cref="ExternalLogLevelFilter"/>.
        /// </remarks>
        /// <param name="level">Log level.</param>
        /// <param name="message">String message.</param>
        /// <param name="ex">Optional exception.</param>
        public void ExternalLog( LogLevel level, string message, Exception? ex = null ) => ExternalLog( level, null, message, ex ); 

        /// <summary>
        /// Gets a cancellation token that is cancelled at the start
        /// of <see cref="Dispose()"/>.
        /// </summary>
        public CancellationToken DisposingToken => _sink.StoppingToken;

        /// <summary>
        /// Gets the sink.
        /// </summary>
        public IGrandOutputSink Sink => _sink;

        void DoGarbageDeadClients()
        {
            lock( _clients )
            {
                for( int i = 0; i < _clients.Count; ++i )
                {
                    if( !_clients[i].TryGetTarget( out GrandOutputClient? cw ) || !cw.IsBoundToMonitor )
                    {
                        _clients.RemoveAt( i-- );
                    }
                }
            }
        }

        /// <summary>
        /// Gets whether this GrandOutput has been disposed.
        /// </summary>
        public bool IsDisposed => !_sink.IsRunning;

        /// <summary>
        /// Closes this <see cref="GrandOutput"/>.
        /// If this is the default one that is disposed, <see cref="Default"/> is set to null.
        /// </summary>
        /// <param name="millisecondsBeforeForceClose">Maximal time to wait.</param>
        public void Dispose( int millisecondsBeforeForceClose = Timeout.Infinite )
        {
            if( _sink.Stop() )
            {
                lock( _defaultLock )
                {
                    if( _default == this )
                    {
                        ActivityMonitor.AutoConfiguration -= AutoRegisterDefault;
                        Debug.Assert( _traceListener != null );
                        Trace.Listeners.Remove( _traceListener );
                        _traceListener = null;
                        _default = null;

                    }
                }
                ActivityMonitor.ExternalLog.OnExternalLog -= OnExternalLog;
                SignalClients();
                _sink.Finalize( millisecondsBeforeForceClose );
            }
        }

        void SignalClients()
        {
            lock( _clients )
            {
                for( int i = 0; i < _clients.Count; ++i )
                {
                    if( _clients[i].TryGetTarget( out GrandOutputClient? cw ) && cw.IsBoundToMonitor )
                    {
                        cw.OnGrandOutputDisposedOrMinimalFilterChanged();
                    }
                }
            }
        }

        /// <summary>
        /// Calls <see cref="Dispose(int)"/> with <see cref="Timeout.Infinite"/>.
        /// If this is the default one that is disposed, <see cref="Default"/> is set to null.
        /// </summary>
        public void Dispose()
        {
            Dispose( Timeout.Infinite );
        }


    }
}
