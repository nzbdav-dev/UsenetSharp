using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        // Clean up any existing connection
        CleanupConnection();
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port, cancellationToken);
            _stream = _tcpClient.GetStream();

            if (useSsl)
            {
                var sslStream = new SslStream(_stream, false, (sender, certificate, chain, sslPolicyErrors) =>
                {
                    // ENV vars control SSL behavior
                    var ignoreNameMismatch = Environment.GetEnvironmentVariable("NNTP_TLS_IGNORE_NAME_MISMATCH");
                    var ignoreDomains = Environment.GetEnvironmentVariable("NNTP_TLS_IGNORE_CERT_DOMAINS");

                    // Ignore server name mismatch globally if requested
                    if (!string.IsNullOrWhiteSpace(ignoreNameMismatch) &&
                        (ignoreNameMismatch == "1" || ignoreNameMismatch.Equals("true", StringComparison.OrdinalIgnoreCase)))
                    {
                        sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;
                    }

                    // Ignore cert errors for specific domains
                    if (!string.IsNullOrWhiteSpace(ignoreDomains))
                    {
                        var domains = ignoreDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (domains.Contains(host, StringComparer.OrdinalIgnoreCase))
                        {
                            return true; // bypass all TLS errors for this host
                        }
                    }

                    return sslPolicyErrors == SslPolicyErrors.None;
                });

                await sslStream.AuthenticateAsClientAsync(host, null,
                    System.Security.Authentication.SslProtocols.Tls12 |
                    System.Security.Authentication.SslProtocols.Tls13, true);

                _stream = sslStream;
            }

            // Use Latin1 encoding to preserve exact byte values 0-255 for yEnc-encoded content
            _reader = new StreamReader(_stream, Encoding.Latin1);
            _writer = new StreamWriter(_stream, Encoding.Latin1) { AutoFlush = true };

            // Read the server response
            var response = await ReadLineAsync(_cts.Token);
            var responseCode = ParseResponseCode(response);

            // NNTP servers typically respond with "200" or "201" for successful connection
            if (responseCode != (int)UsenetResponseType.ServerReadyPostingAllowed &&
                responseCode != (int)UsenetResponseType.ServerReadyNoPostingAllowed)
            {
                throw new UsenetConnectionException(response!) { ResponseCode = responseCode };
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }
}
