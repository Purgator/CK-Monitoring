using System;
using System.IO;
using System.IO.Compression;

namespace CK.Monitoring
{
    internal class GZipStreamReader : Stream
    {
        readonly GZipStream _stream;
        long _position;

        public GZipStreamReader( Stream stream )
        {
            _stream = new GZipStream( stream, CompressionMode.Decompress );
        }

        protected override void Dispose( bool disposing )
        {
            if( disposing ) _stream.Dispose();
            base.Dispose( disposing );
        }


        public override void Flush()
        {
            _stream.Flush();
        }
        
        public override int Read( byte[] array, int offset, int count )
        {
            int read = _stream.Read( array, offset, count );
            _position += read;
            return read;
        }
        
        public override long Seek( long offset, SeekOrigin origin )
        {
            return (_position = _stream.Seek( offset, origin ));
        }

        public override void SetLength( long value )
        {
            throw new NotSupportedException();
        }

        public override void Write( byte[] array, int offset, int count )
        {
            throw new NotSupportedException();
        }

        public Stream BaseStream { get { return _stream.BaseStream; } }

        public override bool CanRead { get { return true; } }

        public override bool CanSeek { get { return _stream.CanSeek; } }
      
        public override bool CanWrite { get { return false; } }
        
        public override long Length { get { throw new NotSupportedException(); } }

        public override long Position
        {
            get { return _position; }
            set { throw new NotSupportedException(); }
        }
    }
}

