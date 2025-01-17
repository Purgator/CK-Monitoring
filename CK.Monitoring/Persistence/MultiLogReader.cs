using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using CK.Core;

namespace CK.Monitoring
{
    /// <summary>
    /// This reader process multiples .ckmon files (possibly in different steps: it internally aggregates the result) and can 
    /// create <see cref="ActivityMap"/> objects on demand.
    /// It is a thread safe object (the ActivityMap is immutable).
    /// </summary>
    public sealed partial class MultiLogReader : IDisposable
    {
        readonly ConcurrentDictionary<string,LiveIndexedMonitor> _monitors;
        readonly ConcurrentDictionary<string,RawLogFile> _files;
        readonly ReaderWriterLockSlim _lockWriteRead;

        readonly object _globalInfoLock;
        DateTime _globalFirstEntryTime;
        DateTime _globalLastEntryTime;

        /// <summary>
        /// Events raised (possibly concurrently) each time a new <see cref="ILiveMonitor"/>
        /// appears.
        /// </summary>
        public event Action<ILiveMonitor>? OnLiveMonitorAppeared; 

        internal class LiveIndexedMonitor : ILiveMonitor
        {
            internal readonly List<RawLogFileMonitorOccurence> _files;
            IdentityCard? _identityCard;
            readonly CancellationTokenSource _identityCardCreated;
            internal DateTimeStamp _firstEntryTime;
            internal int _firstDepth;
            internal DateTimeStamp _lastEntryTime;
            internal int _lastDepth;
            internal Dictionary<CKTrait,int>? _tags; 

            internal LiveIndexedMonitor( string monitorId )
            {
                MonitorId = monitorId;
                _files = new List<RawLogFileMonitorOccurence>();
                _firstEntryTime = DateTimeStamp.MaxValue;
                _lastEntryTime = DateTimeStamp.MinValue;
                _identityCardCreated = new CancellationTokenSource();
            }

            public string MonitorId { get; }

            public IdentityCard? IdentityCard => _identityCard;

            public void OnIdentityCardCreated( Action<ILiveMonitor> action )
            {
                _identityCardCreated.Token.UnsafeRegister( _ => action( this ), null );
            }

            internal void Register( RawLogFileMonitorOccurence fileOccurrence, bool newOccurrence, long streamOffset, IMulticastLogEntry log )
            {
                lock( _files )
                {
                    Debug.Assert( newOccurrence == !_files.Contains( fileOccurrence ) ); 
                    if( newOccurrence ) _files.Add( fileOccurrence );
                    if( _firstEntryTime > log.LogTime )
                    {
                        _firstEntryTime = log.LogTime;
                        _firstDepth = log.GroupDepth;
                    }
                    if( _lastEntryTime < log.LogTime )
                    {
                        _lastEntryTime = log.LogTime;
                        _lastDepth = log.GroupDepth;
                    }
                    if( !log.Tags.IsEmpty )
                    {
                        if( _tags == null )
                        {
                            _tags = new Dictionary<CKTrait, int>();
                            foreach( var t in log.Tags.AtomicTraits )
                            {
                                HandleIdentityCardTag( log, t );
                                _tags.Add( t, 1 );
                            }
                        }
                        else
                        {
                            foreach( var t in log.Tags.AtomicTraits )
                            {
                                HandleIdentityCardTag( log, t );
                                _tags.TryGetValue( t, out var count );
                                _tags[t] = count + 1;
                            }
                        }
                    }
                }
            }

            void HandleIdentityCardTag( IMulticastLogEntry log, CKTrait t )
            {
                if( t == IdentityCard.Tags.IdentityCardFull )
                {
                    if( _identityCard == null )
                    {
                        _identityCard = IdentityCard.TryCreate( log.Text );
                        if( _identityCard != null )
                        {
                            _identityCardCreated.Cancel();
                        }
                    }
                    else
                    {
                        var other = IdentityCard.TryCreate( log.Text );
                        if( other != null )
                        {
                            _identityCard.Add( other );
                        }
                    }
                }
                else if( t == IdentityCard.Tags.IdentityCardUpdate )
                {
                    var added = IdentityCard.TryUnpack( log.Text ) as IReadOnlyList<(string, string)>;
                    if( added != null )
                    {
                        if( _identityCard == null )
                        {
                            _identityCard = new IdentityCard();
                            _identityCard.Add( added );
                            _identityCardCreated.Cancel();
                        }
                        else
                        {
                            _identityCard.Add( added );
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Immutable object that describes the occurrence of a Monitor in a <see cref="RawLogFile"/>.
        /// </summary>
        public sealed class RawLogFileMonitorOccurence
        {
            /// <summary>
            /// The <see cref="RawLogFile"/>.
            /// </summary>
            public readonly RawLogFile LogFile;
            /// <summary>
            /// The monitor's identifier.
            /// </summary>
            public readonly string MonitorId;
            /// <summary>
            /// First offset for this <see cref="MonitorId"/> in this <see cref="LogFile"/>.
            /// </summary>
            public readonly long FirstOffset;
            /// <summary>
            /// Last offset for this <see cref="MonitorId"/> in this <see cref="LogFile"/>.
            /// </summary>
            public long LastOffset { get; internal set; }
            /// <summary>
            /// First entry time for this <see cref="MonitorId"/> in this <see cref="LogFile"/>.
            /// </summary>
            public DateTimeStamp FirstEntryTime { get; internal set; }
            /// <summary>
            /// Last entry time for this <see cref="MonitorId"/> in this <see cref="LogFile"/>.
            /// </summary>
            public DateTimeStamp LastEntryTime { get; internal set; }

            /// <summary>
            /// Creates and opens a <see cref="LogReader"/> that reads unicast entries only from this monitor.
            /// The reader is positioned on the entry (i.e. <see cref="LogReader.MoveNext"/> has been called).
            /// </summary>
            /// <param name="streamOffset">Initial stream position.</param>
            /// <returns>A log reader that will read only entries from this monitor.</returns>
            public LogReader CreateFilteredReaderAndMoveTo( long streamOffset )
            {
                if( streamOffset == -1 ) streamOffset = FirstOffset;
                var r = LogReader.Open( LogFile.FileName, streamOffset, new LogReader.MulticastFilter( MonitorId, LastOffset ) );
                if( !r.MoveNext() )
                {
                    r.Dispose();
                    Throw.InvalidDataException( $"Unable to read '{LogFile.FileName}' for monitor '{MonitorId}' from offset {streamOffset}.", r.ReadException );
                }
                return r;
            }

            /// <summary>
            /// Opens a <see cref="LogReader"/> that reads unicast entries only from this monitor and positions it on the first entry
            /// with the given time (i.e. <see cref="LogReader.MoveNext"/> has been called).
            /// </summary>
            /// <param name="logTime">Log time. Must exist in the stream otherwise an exception is thrown.</param>
            /// <returns>A log reader that will read only entries from this monitor.</returns>
            public LogReader CreateFilteredReaderAndMoveTo( DateTimeStamp logTime )
            {
                var r = LogReader.Open( LogFile.FileName, FirstOffset, new LogReader.MulticastFilter( MonitorId, LastOffset ) );
                while( r.MoveNext() && r.Current.LogTime < logTime ) ;
                if( r.ReadException != null || r.BadEndOfFileMarker )
                {
                    r.Dispose();
                    Throw.InvalidDataException( $"Unable to read '{LogFile.FileName}' for monitor '{MonitorId}' from offset {LastOffset}.", r.ReadException );
                }
                return r;
            }

            internal RawLogFileMonitorOccurence( RawLogFile f, string monitorId, long streamOffset )
            {
                LogFile = f;
                MonitorId = monitorId;
                FirstOffset = streamOffset;
                FirstEntryTime = DateTimeStamp.MaxValue;
                LastEntryTime = DateTimeStamp.MinValue;
            }
        }

        /// <summary>
        /// Immutable object that contains a description of the content of a raw log file.
        /// </summary>
        public sealed class RawLogFile
        {
            readonly string _fileName;
            int _fileVersion;
            DateTimeStamp _firstEntryTime;
            DateTimeStamp _lastEntryTime;
            int _totalEntryCount;
            IReadOnlyList<RawLogFileMonitorOccurence>? _monitors;
            Exception? _error;
            bool _badEndOfFile;

            /// <summary>
            /// Gets the file name.
            /// </summary>
            public string FileName => _fileName;

            /// <summary>
            /// Gets the first entry time.
            /// </summary>
            public DateTimeStamp FirstEntryTime => _firstEntryTime; 
            
            /// <summary>
            /// Gets the last entry time.
            /// </summary>
            public DateTimeStamp LastEntryTime => _lastEntryTime;

            /// <summary>
            /// Gets the file version.
            /// </summary>
            public int FileVersion => _fileVersion;
            
            /// <summary>
            /// Gets the total number of entries.
            /// </summary>
            public int TotalEntryCount => _totalEntryCount;
            
            /// <summary>
            /// Gets whether this file does not end with the end of stream marker (a zero byte).
            /// </summary>
            public bool BadEndOfFile => _badEndOfFile;

            /// <summary>
            /// Gets whether no <see cref="Error"/> occurred and there is no <see cref="BadEndOfFile"/>.
            /// </summary>
            public bool IsValidFile => !_badEndOfFile && _error == null;

            /// <summary>
            /// Gets the <see cref="Exception"/> that occurred while reading file.
            /// </summary>
            public Exception? Error => _error;
            
            /// <summary>
            /// Gets the different monitors that appear in this file.
            /// </summary>
            public IReadOnlyList<RawLogFileMonitorOccurence> Monitors => _monitors!;

            internal object? InitializerLock;

            internal RawLogFile( string fileName )
            {
                _fileName = fileName;
                InitializerLock = new object();
                _firstEntryTime = DateTimeStamp.MaxValue;
                _lastEntryTime = DateTimeStamp.MinValue;
            }

            internal void Initialize( MultiLogReader reader )
            {
                try
                {
                    var monitorOccurrences = new Dictionary<string, RawLogFileMonitorOccurence>();
                    var monitorOccurrenceList = new List<RawLogFileMonitorOccurence>();
                    using( var r = LogReader.Open( _fileName ) )
                    {
                        if( r.MoveNext() )
                        {
                            _fileVersion = r.StreamVersion;
                            do
                            {
                                if( r.Current is IMulticastLogEntry log )
                                {
                                    ++_totalEntryCount;
                                    if( _firstEntryTime > log.LogTime ) _firstEntryTime = log.LogTime;
                                    if( _lastEntryTime < log.LogTime ) _lastEntryTime = log.LogTime;
                                    UpdateMonitor( reader, r.StreamOffset, monitorOccurrences, monitorOccurrenceList, log );
                                }
                            }
                            while( r.MoveNext() );
                        }
                        _badEndOfFile = r.BadEndOfFileMarker;
                        _error = r.ReadException;
                    }
                    _monitors = monitorOccurrenceList.ToArray();
                }
                catch( Exception ex )
                {
                    _error = ex;
                }
            }

            void UpdateMonitor( MultiLogReader reader, long streamOffset, Dictionary<string, RawLogFileMonitorOccurence> monitorOccurrence, List<RawLogFileMonitorOccurence> monitorOccurenceList, IMulticastLogEntry log )
            {
                bool newOccurrence = false;
                if( !monitorOccurrence.TryGetValue( log.MonitorId, out RawLogFileMonitorOccurence? occ ) )
                {
                    occ = new RawLogFileMonitorOccurence( this, log.MonitorId, streamOffset );
                    monitorOccurrence.Add( log.MonitorId, occ );
                    monitorOccurenceList.Add( occ );
                    newOccurrence = true;
                }
                if( occ.FirstEntryTime > log.LogTime ) occ.FirstEntryTime = log.LogTime;
                if( occ.LastEntryTime < log.LogTime ) occ.LastEntryTime = log.LogTime;
                occ.LastOffset = streamOffset;
                reader.RegisterOneLog( occ, newOccurrence, streamOffset, log );
            }

            /// <summary>
            /// Overridden to return details about its content.
            /// </summary>
            /// <returns>Detailed string.</returns>
            public override string ToString()
            {
                return String.Format( "File: '{0}' ({1}), from {2} for {3}, Error={4}", FileName, TotalEntryCount, _firstEntryTime, _lastEntryTime.TimeUtc-_firstEntryTime.TimeUtc, _error != null ? _error.ToString() : "None" );
            }
        }

        /// <summary>
        /// Initializes a new <see cref="MultiLogReader"/>.
        /// </summary>
        public MultiLogReader()
        {
            _monitors = new ConcurrentDictionary<string, LiveIndexedMonitor>();
            _files = new ConcurrentDictionary<string, RawLogFile>( StringComparer.OrdinalIgnoreCase );
            _lockWriteRead = new ReaderWriterLockSlim();
            _globalInfoLock = new object();
            _globalFirstEntryTime = DateTime.MaxValue;
            _globalLastEntryTime = DateTime.MinValue;
        }

        /// <summary>
        /// Adds a bunch of log files.
        /// </summary>
        /// <param name="files">Set of files to add.</param>
        /// <returns>List of newly added files (already known files are skipped).</returns>
        public List<RawLogFile> Add( IEnumerable<string> files )
        {
            List<RawLogFile> result = new List<RawLogFile>();
            System.Threading.Tasks.Parallel.ForEach( files, s =>
            {
                var f = Add( s, out bool newOne );
                lock( result )
                {
                    if( !result.Contains( f ) ) result.Add( f );
                }
            } );
            return result;
        }

        /// <summary>
        /// Adds a file to this reader. This is thread safe (can be called from any thread at any time). 
        /// </summary>
        /// <param name="filePath">The path of the file to add.</param>
        /// <param name="newFileIndex">True if the file has actually been added, false if it was already added.</param>
        /// <returns>The RawLogFile object (newly created or already existing).</returns>
        public RawLogFile Add( string filePath, out bool newFileIndex )
        {
            newFileIndex = false;
            filePath = FileUtil.NormalizePathSeparator( filePath, false );
            _lockWriteRead.EnterReadLock();
            RawLogFile f = _files.GetOrAdd( filePath, fileName => new RawLogFile( fileName ) );
            var l = f.InitializerLock;
            if( l != null )
            {
                lock( l )
                {
                    if( f.InitializerLock != null )
                    {
                        newFileIndex = true;
                        f.Initialize( this );
                        f.InitializerLock = null;
                    }
                }
            }
            if( newFileIndex )
            {
                lock( _globalInfoLock )
                {
                    if( _globalFirstEntryTime > f.FirstEntryTime.TimeUtc ) _globalFirstEntryTime = f.FirstEntryTime.TimeUtc;
                    if( _globalLastEntryTime < f.LastEntryTime.TimeUtc ) _globalLastEntryTime = f.LastEntryTime.TimeUtc;
                }
            }
            _lockWriteRead.ExitReadLock();
            return f;
        }

        LiveIndexedMonitor RegisterOneLog( RawLogFileMonitorOccurence fileOccurrence, bool newOccurrence, long streamOffset, IMulticastLogEntry log )
        {
            Debug.Assert( fileOccurrence.MonitorId == log.MonitorId );
            Debug.Assert( !newOccurrence || (fileOccurrence.FirstEntryTime == log.LogTime && fileOccurrence.LastEntryTime == log.LogTime ) );
            // This is required to detect the fact that it's this call that created the final monitor (the GetOrAdd may call the
            // value factory but another thread may have already set it) and we must ensure that the OnNewLiveMonitor is
            // called once and only once per new live monitor.
            LiveIndexedMonitor? proposal = null;
            LiveIndexedMonitor m = _monitors.GetOrAdd( log.MonitorId, id => proposal = new LiveIndexedMonitor( id ) );
            m.Register( fileOccurrence, newOccurrence, streamOffset, log );
            if( proposal == m )
            {
                OnLiveMonitorAppeared?.Invoke( m );
            }
            return m;
        }

        /// <summary>
        /// Releases this reader.
        /// </summary>
        public void Dispose()
        {
            _lockWriteRead.Dispose();
        }

    }

}
