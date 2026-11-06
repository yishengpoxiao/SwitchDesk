using System.IO;
using System.Text.Json;
using SwitchDesk.Core;

namespace SwitchDesk.Tests;
internal static partial class Program
{
    private static async Task InstallPolicyTestsAsync()
    {
        var file = Path.Combine(root, "install-policy.nsp");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var warning = new InstallWarning("NcaHashMismatch", new string('A', 32) + ".nca", new string('A', 32), new string('B', 64));
        var signatureWarning = new InstallWarning("NcaHeaderSignatureMismatch", "converted.cnmt.nca", "", "",
            new string('C', 64), "00000000000081F9");
        await Test("manual compatibility requests warn and returns complete visible NCA digest differences", async () =>
        {
            await using var peer = new Peer { InstallationWarnings = [warning] }; await using var client = await peer.ConnectAsync();
            var result = await Transfers.InstallAsync(client, file, "SdCard", null, default, policy: NcaHashPolicy.Warn);
            Assert(peer.RequestedNcaPolicies.SequenceEqual(["warn"]) && peer.InstalledNames.Count == 1);
            Assert(result.Warnings.SequenceEqual([warning]) && result.Warnings[0].Message.Contains(warning.ActualSha256));
        });
        await Test("compatibility requires explicit companion capability before sending an install", async () =>
        {
            await using var peer = new Peer { DeviceOverride = Peer.Info with { Capabilities = Peer.Info.Capabilities.Where(x => x != "install.hash-warning.v1").ToArray() } };
            await using var client = await peer.ConnectAsync();
            await Throws<DeviceException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default, policy: NcaHashPolicy.Warn));
            Assert(peer.InstalledNames.Count == 0 && peer.RequestedNcaPolicies.Count == 0 && peer.RequestedSignaturePolicies.Count == 0 && !client.IsBroken);
        });
        await Test("default installs remain strict and invalid policies never reach the device", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            var result = await Transfers.InstallAsync(client, file, "SdCard", null, default);
            Assert(result.Warnings.Length == 0 && peer.RequestedNcaPolicies.SequenceEqual(["strict"]) && peer.RequestedSignaturePolicies.SequenceEqual(["strict"]));
            await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default, policy: (NcaHashPolicy)99));
            await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default, signaturePolicy: (NcaSignaturePolicy)99));
            Assert(peer.InstalledNames.Count == 1);
        });
        await Test("compatibility preserves the downloaded file digest and size requirements", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default, new string('0',64), 3, NcaHashPolicy.Warn));
            await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default, expectedLength: 4, policy: NcaHashPolicy.Warn));
            Assert(peer.InstalledNames.Count == 0);
        });
        await Test("strict installs never silently accept a warning policy response", async () =>
        {
            await using var peer = new Peer { InstallationWarnings = [warning] }; await using var client = await peer.ConnectAsync();
            await Throws<DeviceException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default));
            Assert(peer.RequestedNcaPolicies.SequenceEqual(["strict"]));
        });
        await Test("malformed compatibility warning responses never become silent success", async () =>
        {
            object[] invalid = [new { }, new { warnings = "bad" }, new { warnings = new object?[] { null } },
                new { warnings = new[] { warning with { Code = "Unknown" } } },
                new { warnings = new[] { warning with { Entry = "bad\nname" } } },
                new { warnings = new[] { warning with { ExpectedContentId = "short" } } },
                new { warnings = new[] { warning with { ActualSha256 = new string('A', 64) } } }];
            foreach (var result in invalid)
            {
                var stream = new Audit2Duplex(await Audit2Responses(Peer.Info, result));
                await using var client = new DeviceClient(stream);
                await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default, policy: NcaHashPolicy.Warn));
                Assert((await stream.RequestMethodsAsync()).SequenceEqual(["device.info", "titles.install"]));
            }
        });
        await Test("signature compatibility preserves the exact verification result and complete header digest", async () =>
        {
            var response = new { warnings = new[] { new { code = signatureWarning.Code, entry = signatureWarning.Entry,
                headerSha256 = signatureWarning.HeaderSha256!.ToLowerInvariant(), verificationResult = "00000000000081f9" } } };
            var stream = new Audit2Duplex(await Audit2Responses(Peer.Info, response));
            await using var client = new DeviceClient(stream);
            var result = await Transfers.InstallAsync(client, file, "SdCard", null, default, signaturePolicy: NcaSignaturePolicy.Warn);
            Assert(result.Warnings.SequenceEqual([signatureWarning]));
            Assert(result.Warnings[0].Message.Contains(signatureWarning.Entry, StringComparison.Ordinal)
                && result.Warnings[0].Message.Contains(signatureWarning.HeaderSha256!, StringComparison.Ordinal)
                && result.Warnings[0].Message.Contains(signatureWarning.VerificationResult!, StringComparison.Ordinal));
            Assert((await stream.RequestMethodsAsync()).SequenceEqual(["device.info", "titles.install"]));
        });
        await Test("both independent warning policies retain both NCA warning types", async () =>
        {
            await using var peer = new Peer { InstallationWarnings = [warning, signatureWarning] };
            await using var client = await peer.ConnectAsync();
            var result = await Transfers.InstallAsync(client, file, "SdCard", null, default,
                policy: NcaHashPolicy.Warn, signaturePolicy: NcaSignaturePolicy.Warn);
            Assert(peer.RequestedNcaPolicies.SequenceEqual(["warn"]) && peer.RequestedSignaturePolicies.SequenceEqual(["warn"]));
            Assert(result.Warnings.SequenceEqual([warning, signatureWarning]));
        });
        await Test("signature compatibility requires its own capability before any install request", async () =>
        {
            foreach (var hashPolicy in new[] { NcaHashPolicy.Strict, NcaHashPolicy.Warn })
            {
                await using var peer = new Peer { DeviceOverride = Peer.Info with
                    { Capabilities = Peer.Info.Capabilities.Where(x => x != "install.signature-warning.v1").ToArray() } };
                await using var client = await peer.ConnectAsync();
                await Throws<DeviceException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default,
                    policy: hashPolicy, signaturePolicy: NcaSignaturePolicy.Warn));
                Assert(peer.InstalledNames.Count == 0 && peer.RequestedNcaPolicies.Count == 0 && peer.RequestedSignaturePolicies.Count == 0 && !client.IsBroken);
            }
        });
        await Test("warning capabilities are independent and do not weaken the other policy", async () =>
        {
            await using (var peer = new Peer { InstallationWarnings = [signatureWarning], DeviceOverride = Peer.Info with
                { Capabilities = Peer.Info.Capabilities.Where(x => x != "install.hash-warning.v1").ToArray() } })
            {
                await using var client = await peer.ConnectAsync();
                var result = await Transfers.InstallAsync(client, file, "SdCard", null, default, signaturePolicy: NcaSignaturePolicy.Warn);
                Assert(result.Warnings.SequenceEqual([signatureWarning]) && peer.RequestedNcaPolicies.SequenceEqual(["strict"])
                    && peer.RequestedSignaturePolicies.SequenceEqual(["warn"]));
            }
            await using (var peer = new Peer { InstallationWarnings = [warning], DeviceOverride = Peer.Info with
                { Capabilities = Peer.Info.Capabilities.Where(x => x != "install.signature-warning.v1").ToArray() } })
            {
                await using var client = await peer.ConnectAsync();
                var result = await Transfers.InstallAsync(client, file, "SdCard", null, default, policy: NcaHashPolicy.Warn);
                Assert(result.Warnings.SequenceEqual([warning]) && peer.RequestedNcaPolicies.SequenceEqual(["warn"])
                    && peer.RequestedSignaturePolicies.SequenceEqual(["strict"]));
            }
        });
        await Test("signature warning mode does not accept content digest mismatches in strict hash mode", async () =>
        {
            await using var peer = new Peer { InstallationWarnings = [warning, signatureWarning] };
            await using var client = await peer.ConnectAsync();
            try
            {
                await Transfers.InstallAsync(client, file, "SdCard", null, default, signaturePolicy: NcaSignaturePolicy.Warn);
                Assert(false, "strict hash policy accepted a digest mismatch");
            }
            catch (DeviceException ex) { Assert(ex.Code == "PackageHashMismatch"); }
            Assert(peer.RequestedNcaPolicies.SequenceEqual(["strict"]) && peer.RequestedSignaturePolicies.SequenceEqual(["warn"]));
        });
        await Test("strict signature mode rejects signature warnings even when hash warnings are allowed", async () =>
        {
            foreach (var hashPolicy in new[] { NcaHashPolicy.Strict, NcaHashPolicy.Warn })
            {
                await using var peer = new Peer { InstallationWarnings = [signatureWarning] };
                await using var client = await peer.ConnectAsync();
                try
                {
                    await Transfers.InstallAsync(client, file, "SdCard", null, default, policy: hashPolicy);
                    Assert(false, "strict signature policy accepted a signature mismatch");
                }
                catch (DeviceException ex) { Assert(ex.Code == "PackageSignatureMismatch"); }
                Assert(peer.RequestedSignaturePolicies.SequenceEqual(["strict"]));
            }
        });
        await Test("signature compatibility still checks the selected file digest and length", async () =>
        {
            await using var peer = new Peer(); await using var client = await peer.ConnectAsync();
            await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default,
                new string('0', 64), 3, signaturePolicy: NcaSignaturePolicy.Warn));
            await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default,
                expectedLength: 4, signaturePolicy: NcaSignaturePolicy.Warn));
            Assert(peer.InstalledNames.Count == 0 && peer.RequestedSignaturePolicies.Count == 0 && !client.IsBroken);
        });
        await Test("malformed or non-81F9 signature warnings never become successful compatibility results", async () =>
        {
            object[] invalid = [new { }, new { warnings = "bad" }, new { warnings = new object?[] { null } },
                new { warnings = new[] { signatureWarning with { Code = "Unknown" } } },
                new { warnings = new[] { signatureWarning with { Entry = "bad\nname" } } },
                new { warnings = new[] { signatureWarning with { HeaderSha256 = null } } },
                new { warnings = new[] { signatureWarning with { HeaderSha256 = new string('G', 64) } } },
                new { warnings = new[] { signatureWarning with { HeaderSha256 = new string('C', 63) } } },
                new { warnings = new[] { signatureWarning with { VerificationResult = null } } },
                new { warnings = new[] { signatureWarning with { VerificationResult = "81F9" } } },
                new { warnings = new[] { signatureWarning with { VerificationResult = "0000000000000000" } } },
                new { warnings = new[] { signatureWarning with { VerificationResult = "000000000000A5F9" } } },
                new { warnings = new[] { signatureWarning with { VerificationResult = "00000000000083F9" } } },
                new { warnings = new[] { new { code = signatureWarning.Code, entry = signatureWarning.Entry,
                    headerSha256 = signatureWarning.HeaderSha256, verificationResult = 0x81F9 } } }];
            foreach (var response in invalid)
            {
                var stream = new Audit2Duplex(await Audit2Responses(Peer.Info, response));
                await using var client = new DeviceClient(stream);
                await Throws<InvalidDataException>(() => Transfers.InstallAsync(client, file, "SdCard", null, default,
                    policy: NcaHashPolicy.Warn, signaturePolicy: NcaSignaturePolicy.Warn));
                Assert((await stream.RequestMethodsAsync()).SequenceEqual(["device.info", "titles.install"]));
            }
        });
        await Test("legacy strict responses remain valid without signature warning capability", async () =>
        {
            var stream = new Audit2Duplex(await Audit2Responses(new { }));
            await using var client = new DeviceClient(stream);
            var result = await Transfers.InstallAsync(client, file, "SdCard", null, default);
            Assert(result.Warnings.Length == 0 && (await stream.RequestMethodsAsync()).SequenceEqual(["titles.install"]));
        });
    }
}
