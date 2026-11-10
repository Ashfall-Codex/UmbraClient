﻿namespace UmbraSync.WebAPI.Files;

// Adapté de https://github.com/bezzad/Downloader (licence MIT)
internal sealed class ThrottledStream : Stream
{
    private const int IntervalsPerSecond = 20;
    private const int MaxSleepPerReadMs = 1000;

    public static long Infinite => long.MaxValue;

    private readonly Stream _baseStream;
    private readonly Bandwidth _bandwidth = new();
    private readonly Lock _stateLock = new();
    private long _bandwidthLimit;
    private CancellationTokenSource _bandwidthChangeTokenSource = new();
    private bool _disposed;

    public ThrottledStream(Stream baseStream, long bandwidthLimit)
    {
        ArgumentNullException.ThrowIfNull(baseStream);
        ArgumentOutOfRangeException.ThrowIfNegative(bandwidthLimit);

        _baseStream = baseStream;
        BandwidthLimit = bandwidthLimit;
    }

    public long BandwidthLimit
    {
        get => Volatile.Read(ref _bandwidthLimit);
        set
        {
            var normalized = value <= 0 ? Infinite : value;
            CancellationTokenSource previous;
            lock (_stateLock)
            {
                if (_disposed || _bandwidthLimit == normalized) return;
                Volatile.Write(ref _bandwidthLimit, normalized);
                _bandwidth.BandwidthLimit = normalized;
                _bandwidth.Reset();
                previous = _bandwidthChangeTokenSource;
                _bandwidthChangeTokenSource = new CancellationTokenSource();
            }

            previous.Cancel();
            previous.Dispose();
        }
    }

    public override bool CanRead => _baseStream.CanRead;

    public override bool CanSeek => _baseStream.CanSeek;

    public override bool CanWrite => _baseStream.CanWrite;

    public override long Length => _baseStream.Length;

    public override long Position
    {
        get => _baseStream.Position;
        set => _baseStream.Position = value;
    }

    public override void Flush() => _baseStream.Flush();

    public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);

    public override void SetLength(long value) => _baseStream.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (BandwidthLimit == Infinite)
            return _baseStream.Read(buffer, offset, count);

        var read = _baseStream.Read(buffer, offset, LimitedReadSize(count));
        Throttle(read, CancellationToken.None).GetAwaiter().GetResult();
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (BandwidthLimit == Infinite)
            return await _baseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        var read = await _baseStream.ReadAsync(buffer[..LimitedReadSize(buffer.Length)], cancellationToken).ConfigureAwait(false);
        await Throttle(read, cancellationToken).ConfigureAwait(false);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Throttle(count, CancellationToken.None).GetAwaiter().GetResult();
        _baseStream.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Throttle(buffer.Length, cancellationToken).ConfigureAwait(false);
        await _baseStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override string ToString() => _baseStream.ToString() ?? string.Empty;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancellationTokenSource? tokenSource = null;
            lock (_stateLock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    tokenSource = _bandwidthChangeTokenSource;
                }
            }

            if (tokenSource != null)
            {
                try
                {
                    tokenSource.Cancel();
                }
                finally
                {
                    tokenSource.Dispose();
                    _baseStream.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    private int LimitedReadSize(int requested)
    {
        var limit = BandwidthLimit;
        return limit == Infinite ? requested : (int)Math.Min(requested, Math.Max(1, limit / IntervalsPerSecond));
    }

    private async Task Throttle(int transmissionVolume, CancellationToken token)
    {
        if (transmissionVolume <= 0 || BandwidthLimit == Infinite) return;

        _bandwidth.CalculateSpeed(transmissionVolume);
        await Sleep(Math.Min(_bandwidth.PopSpeedRetrieveTime(), MaxSleepPerReadMs), token).ConfigureAwait(false);
    }

    private async Task Sleep(int time, CancellationToken token)
    {
        if (time <= 0) return;

        CancellationTokenSource linked;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, _bandwidthChangeTokenSource.Token);
        }

        using (linked)
        {
            try
            {
                await Task.Delay(time, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            }
        }
    }

    private sealed class Bandwidth
    {
        private int _lastSecondCheckpoint;
        private long _lastTransferredBytesCount;
        private int _speedRetrieveTime;
        private long _bandwidthLimit = long.MaxValue;

        public Bandwidth() => SecondCheckpoint();

        public long BandwidthLimit
        {
            get => Volatile.Read(ref _bandwidthLimit);
            set => Volatile.Write(ref _bandwidthLimit, value);
        }

        public void CalculateSpeed(long receivedBytesCount)
        {
            int elapsedTime = Environment.TickCount - Volatile.Read(ref _lastSecondCheckpoint) + 1;
            receivedBytesCount = Interlocked.Add(ref _lastTransferredBytesCount, receivedBytesCount);
            double momentSpeed = receivedBytesCount * 1000 / (double)elapsedTime;

            if (elapsedTime > 1000)
                SecondCheckpoint();

            var limit = BandwidthLimit;
            if (momentSpeed >= limit)
            {
                var expectedTime = Math.Min(receivedBytesCount * 1000 / limit, int.MaxValue);
                Interlocked.Add(ref _speedRetrieveTime, (int)Math.Clamp(expectedTime - elapsedTime, int.MinValue, int.MaxValue));
            }
        }

        public int PopSpeedRetrieveTime() => Interlocked.Exchange(ref _speedRetrieveTime, 0);

        public void Reset()
        {
            SecondCheckpoint();
            Interlocked.Exchange(ref _speedRetrieveTime, 0);
        }

        private void SecondCheckpoint()
        {
            Interlocked.Exchange(ref _lastSecondCheckpoint, Environment.TickCount);
            Interlocked.Exchange(ref _lastTransferredBytesCount, 0);
        }
    }
}