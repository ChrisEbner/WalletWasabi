using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using WalletWasabi.Tor.Socks5.Pool;

namespace WalletWasabi.Tor.Socks5
{
	/// <summary>
	/// Wraps a TCP connection to Tor SOCKS5 endpoint.
	/// </summary>
	public class TorTcpConnection : IDisposable
	{
		private volatile bool _disposedValue = false;

		/// <summary>ID generator.</summary>
		private static long LastId;

		/// <param name="tcpClient">TCP client connected to Tor SOCKS5 endpoint.</param>
		/// <param name="transportStream">Transport stream to actually send the data to Tor SOCKS5 endpoint (the difference is SSL).</param>
		/// <param name="allowRecycling">Whether it is allowed to re-use this Tor pool item.</param>
		public TorTcpConnection(TcpClient tcpClient, Stream transportStream, bool allowRecycling)
		{
			TcpClient = tcpClient;
			TransportStream = transportStream;
			AllowRecycling = allowRecycling;
			Id = Interlocked.Increment(ref LastId);
		}

		/// <remarks>Lock object to guard <see cref="State"/> property.</remarks>
		private object StateLock { get; } = new();

		/// <remarks>All access to this property must be guarded by <see cref="StateLock"/>.</remarks>
		public TcpConnectionState State { get; private set; }

		/// <summary>Gets whether this pool item can be potentially re-used.</summary>
		private bool AllowRecycling { get; }

		/// <summary>Gets whether internal <see cref="TcpClient"/> can be re-used for a new HTTP(s) request.</summary>
		/// <returns><c>true</c> when <see cref="TorTcpConnection"/> must be disposed, <c>false</c> otherwise.</returns>
		public bool NeedDisposal => State == TcpConnectionState.ToDispose;

		/// <summary>Unique identifier of the pool item for logging purposes.</summary>
		private long Id { get; }

		/// <summary>TCP connection to Tor's SOCKS5 server.</summary>
		private TcpClient TcpClient { get; }

		/// <summary>Transport stream for sending  HTTP/HTTPS requests through Tor's SOCKS5 server.</summary>
		/// <remarks>This stream is not to be used to send commands to Tor's SOCKS5 server.</remarks>
		private Stream TransportStream { get; }

		/// <summary>
		/// Stream to transport HTTP(s) request.
		/// </summary>
		/// <remarks>Either <see cref="TcpClient.GetStream"/> or <see cref="SslStream"/> over <see cref="TcpClient.GetStream"/>.</remarks>
		public Stream GetTransportStream() => TransportStream;

		/// <summary>Reserve the pool item for an HTTP(s) request so no other consumer can use this pool item.</summary>
		public bool TryReserve()
		{
			if (State == TcpConnectionState.FreeToUse)
			{
				State = TcpConnectionState.InUse;
				return true;
			}

			return false;
		}

		/// <summary>
		/// After the <see cref="TorTcpConnection"/> is used to send an HTTP(s) request, it needs to be unreserved
		/// so that the pool item can be used again.
		/// </summary>
		/// <returns>Pool item state after unreserve operation.</returns>
		public TcpConnectionState Unreserve()
		{
			lock (StateLock)
			{
				Debug.Assert(State == TcpConnectionState.InUse, $"Unexpected state: '{State}'.");
				State = AllowRecycling ? TcpConnectionState.FreeToUse : TcpConnectionState.ToDispose;
				return State;
			}
		}

		/// <inheritdoc/>
		public override string ToString()
		{
			return $"PoolConnection#{Id}";
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposedValue)
			{
				if (disposing)
				{
					TcpClient?.Dispose();
				}
				_disposedValue = true;
			}
		}

		/// <summary>
		/// This code added to correctly implement the disposable pattern.
		/// </summary>
		public void Dispose()
		{
			// Do not change this code. Put cleanup code in Dispose(bool disposing) above.
			Dispose(true);
		}
	}
}
