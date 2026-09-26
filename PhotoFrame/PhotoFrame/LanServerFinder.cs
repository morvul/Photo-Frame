using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoFrame
{
    /// <summary>
    /// Ищет в домашней сети сервер Immich или Home Assistant, чтобы не набирать адрес
    /// пультом по экранной клавиатуре.
    /// </summary>
    /// <remarks>
    /// Тот же приём, что и в scripts/lan-discover.sh для поиска рамок по adb: сканируется
    /// не настоящая подсеть устройства (маска на Android отдаётся ненадёжно), а всегда /24
    /// вокруг собственного IP-адреса — рамки и серверы стоят в домашней сети за одним
    /// роутером, а не за VPN с маской /8, где сканировать было бы нечего.
    /// </remarks>
    public sealed class LanServerFinder
    {
        /// <summary>Сколько адресов проверяется одновременно.</summary>
        private const int ScanConcurrency = 64;

        /// <summary>Сколько ждать открытия TCP-порта на одном адресе.</summary>
        private static readonly TimeSpan PortProbeTimeout = TimeSpan.FromMilliseconds(250);

        /// <summary>Сколько ждать ответа при проверке, что сервер — именно тот, кого ищем.</summary>
        private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(3);

        /// <summary>Ищет Immich: порт по умолчанию, подтверждение — /api/server/ping.</summary>
        public Task<List<string>> FindImmichServersAsync(CancellationToken cancellationToken = default) =>
            FindServersAsync(ImmichCatalog.DefaultPort, IsImmichAsync, cancellationToken);

        /// <summary>Ищет Home Assistant: порт по умолчанию, подтверждение — /manifest.json.</summary>
        public Task<List<string>> FindHomeAssistantServersAsync(
            CancellationToken cancellationToken = default) =>
            FindServersAsync(HomeAssistantDiscovery.DefaultPort, IsHomeAssistantAsync, cancellationToken);

        /// <summary>
        /// Сканирует свою /24 на заданном порту и подтверждает каждый ответивший адрес.
        /// </summary>
        /// <returns>Адреса вида http://ip:port, подтверждённые проверкой.</returns>
        private async Task<List<string>> FindServersAsync(
            int port,
            Func<IPAddress, int, CancellationToken, Task<bool>> verifyAsync,
            CancellationToken cancellationToken)
        {
            IPAddress? localAddress = GetLocalIPv4Address();
            if (localAddress is null)
            {
                return new List<string>();
            }

            byte[] addressBytes = localAddress.GetAddressBytes();
            string subnetPrefix = $"{addressBytes[0]}.{addressBytes[1]}.{addressBytes[2]}";

            List<IPAddress> hostsWithOpenPort =
                await ScanSubnetForOpenPortAsync(subnetPrefix, port, cancellationToken).ConfigureAwait(false);

            var confirmedServers = new List<string>();

            foreach (IPAddress candidate in hostsWithOpenPort)
            {
                if (await verifyAsync(candidate, port, cancellationToken).ConfigureAwait(false))
                {
                    confirmedServers.Add($"http://{candidate}:{port}");
                }
            }

            return confirmedServers;
        }

        /// <summary>
        /// Свой IPv4-адрес в локальной сети. Предпочитает Wi-Fi — на рамке это всегда
        /// единственная сеть, ведущая к остальным устройствам дома.
        /// </summary>
        private static IPAddress? GetLocalIPv4Address()
        {
            var candidates = new List<(string InterfaceName, IPAddress Address)>();

            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicastAddress in
                    networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        candidates.Add((networkInterface.Name, unicastAddress.Address));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            foreach ((string interfaceName, IPAddress address) in candidates)
            {
                if (interfaceName.Contains("wlan", StringComparison.OrdinalIgnoreCase))
                {
                    return address;
                }
            }

            return candidates[0].Address;
        }

        private static async Task<List<IPAddress>> ScanSubnetForOpenPortAsync(
            string subnetPrefix, int port, CancellationToken cancellationToken)
        {
            var hostsWithOpenPort = new List<IPAddress>();
            var scanLock = new object();

            using var concurrencyLimiter = new SemaphoreSlim(ScanConcurrency);

            var probes = new List<Task>(254);
            for (int lastOctet = 1; lastOctet <= 254; lastOctet++)
            {
                var candidate = IPAddress.Parse($"{subnetPrefix}.{lastOctet}");
                probes.Add(ProbeOneHostAsync(candidate));
            }

            await Task.WhenAll(probes).ConfigureAwait(false);
            return hostsWithOpenPort;

            async Task ProbeOneHostAsync(IPAddress candidate)
            {
                await concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (await IsPortOpenAsync(candidate, port, cancellationToken).ConfigureAwait(false))
                    {
                        lock (scanLock)
                        {
                            hostsWithOpenPort.Add(candidate);
                        }
                    }
                }
                finally
                {
                    concurrencyLimiter.Release();
                }
            }
        }

        private static async Task<bool> IsPortOpenAsync(
            IPAddress address, int port, CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(PortProbeTimeout);

            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(address, port, timeoutSource.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception connectFailure) when (
                connectFailure is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                return false;
            }
        }

        private static async Task<bool> IsImmichAsync(
            IPAddress address, int port, CancellationToken cancellationToken)
        {
            string serverUrl = $"http://{address}:{port}";

            string? pingBody = await TryGetAsync(ImmichCatalog.BuildPingUrl(serverUrl), cancellationToken)
                .ConfigureAwait(false);

            if (pingBody is not null && ImmichCatalog.IsPingResponse(pingBody))
            {
                return true;
            }

            // Серверы до переезда с server-info на server отвечают только по старому пути.
            string? legacyPingBody = await TryGetAsync(
                ImmichCatalog.BuildLegacyPingUrl(serverUrl), cancellationToken).ConfigureAwait(false);

            return legacyPingBody is not null && ImmichCatalog.IsPingResponse(legacyPingBody);
        }

        private static async Task<bool> IsHomeAssistantAsync(
            IPAddress address, int port, CancellationToken cancellationToken)
        {
            string manifestBody = await TryGetAsync(
                $"http://{address}:{port}/manifest.json", cancellationToken).ConfigureAwait(false)
                ?? string.Empty;

            return HomeAssistantDiscovery.IsHomeAssistantManifest(manifestBody);
        }

        /// <summary>Тело ответа либо null — недоступный адрес — самый частый исход при сканировании.</summary>
        private static async Task<string?> TryGetAsync(string url, CancellationToken cancellationToken)
        {
            using var httpClient = new HttpClient { Timeout = VerifyTimeout };

            try
            {
                return await httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception requestFailure) when (
                requestFailure is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                return null;
            }
        }
    }
}
