using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.2", "1.0.1", true)]
    [InlineData("1.1", "1.0.9", true)]
    [InlineData("v2.0.0-beta.1", "1.9.0", true)]
    [InlineData("v1.0.1", "1.0.1", false)]
    [InlineData("v1.0.0", "1.0.1", false)]
    [InlineData("nightly", "1.0.1", false)]
    [InlineData(null, "1.0.1", false)]
    public void Release_tags_compare_by_version(string? tag, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(tag, current));

    /// <summary>Filee works offline: a failed update check must neither throw nor report an update.</summary>
    public static TheoryData<string> OfflineFailures => ["no network", "timeout", "dropped while reading", "not json"];

    [Theory]
    [MemberData(nameof(OfflineFailures))]
    public async Task Update_check_without_internet_is_quiet(string failure)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeNetwork(failure));

        var found = await updates.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(found);
        Assert.False(updates.IsUpdateAvailable);
    }

    private sealed class FakeNetwork(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => failure switch
        {
            "no network" => throw new HttpRequestException("No such host is known.", new SocketException((int)SocketError.HostNotFound)),
            "timeout" => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
            "dropped while reading" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html>") }),
        };
    }

    /// <summary>A response body whose connection breaks after the headers.</summary>
    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection was reset.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ───────── In-app update: find, download and verify the installer ─────────

    private const string Base = "https://example.test/v9.9.9/";

    [Fact]
    public void Release_files_are_the_installer_and_the_checksums()
    {
        using var release = JsonDocument.Parse($$"""
            {"tag_name": "v9.9.9", "assets": [
              {"name": "Filee-9.9.9-SHA256SUMS.txt", "size": 189, "browser_download_url": "{{Base}}Filee-9.9.9-SHA256SUMS.txt"},
              {"name": "Filee-9.9.9-win-Portable.zip", "size": 5, "browser_download_url": "{{Base}}Filee-9.9.9-win-Portable.zip"},
              {"name": "Filee-9.9.9-win-Setup.exe", "size": 1234, "browser_download_url": "{{Base}}Filee-9.9.9-win-Setup.exe"}
            ]}
            """);

        var (installer, checksums) = UpdateService.FindFiles(release.RootElement);

        Assert.Equal(new UpdateService.ReleaseFile("Filee-9.9.9-win-Setup.exe", Base + "Filee-9.9.9-win-Setup.exe", 1234), installer);
        Assert.Equal("Filee-9.9.9-SHA256SUMS.txt", checksums?.Name);
    }

    [Fact]
    public void Release_without_files_or_with_plain_http_links_has_no_installer()
    {
        using var empty = JsonDocument.Parse("""{"tag_name": "v9.9.9"}""");
        using var http = JsonDocument.Parse("""
            {"assets": [{"name": "Filee-9.9.9-win-Setup.exe", "size": 1, "browser_download_url": "http://example.test/a.exe"}]}
            """);

        Assert.Equal((null, null), UpdateService.FindFiles(empty.RootElement));
        Assert.Null(UpdateService.FindFiles(http.RootElement).Installer);
    }

    [Fact]
    public void Checksum_lines_are_matched_by_file_name()
    {
        var hash = new string('A', 64);
        var sums = $"{new string('1', 64)}  Filee-9.9.9-win-Portable.zip\r\n{hash}  Filee-9.9.9-win-Setup.exe\r\n";

        Assert.Equal(hash.ToLowerInvariant(), UpdateService.ParseChecksum(sums, "Filee-9.9.9-win-Setup.exe"));
        Assert.Equal(hash.ToLowerInvariant(), UpdateService.ParseChecksum($"{hash} *Filee-9.9.9-win-Setup.exe", "Filee-9.9.9-win-Setup.exe"));
        Assert.Null(UpdateService.ParseChecksum(sums, "Filee-9.9.8-win-Setup.exe"));
        Assert.Null(UpdateService.ParseChecksum("tooshort  Filee-9.9.9-win-Setup.exe", "Filee-9.9.9-win-Setup.exe"));
    }

    [Fact]
    public async Task Installer_is_downloaded_verified_and_reused()
    {
        var installer = RandomNumberGenerator.GetBytes(300_000);
        var network = new FakeRelease(installer, Sha256(installer));
        var updates = Updates(network, installer.Length);
        var folder = TempFolder();
        File.WriteAllText(Path.Combine(folder, "Filee-9.9.8-win-Setup.exe"), "an older download");
        var progress = new List<int>();

        var path = await updates.DownloadInstallerAsync(folder, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(folder, "Filee-9.9.9-win-Setup.exe"), path);
        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(folder)); // the older download and the .part file are gone
        Assert.Equal(100, progress[^1]);
        Assert.True(progress.SequenceEqual(progress.Order()), "progress only goes up");

        // Pressing Update again (e.g. after saying no to the administrator prompt) does not download it again.
        await updates.DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, network.InstallerRequests);
    }

    [Fact]
    public async Task Installer_with_the_wrong_checksum_is_thrown_away()
    {
        var installer = RandomNumberGenerator.GetBytes(10_000);
        var updates = Updates(new FakeRelease(installer, new string('0', 64)), installer.Length);
        var folder = TempFolder();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updates.DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Cut_off_download_fails()
    {
        var installer = RandomNumberGenerator.GetBytes(10_000);
        // GitHub said the file is bigger than what arrived.
        var updates = Updates(new FakeRelease(installer, Sha256(installer)), installer.Length + 500);

        await Assert.ThrowsAsync<IOException>(() =>
            updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Checksum_file_without_the_installer_fails()
    {
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var updates = Updates(new FakeRelease(installer, Sha256(installer), sumsName: "Filee-9.9.8-win-Setup.exe"), installer.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_errors_are_reported_as_http_errors()
    {
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var updates = Updates(new FakeRelease(installer, Sha256(installer), installerStatus: HttpStatusCode.NotFound), installer.Length);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Installer_runs_without_the_wizard_and_without_restarting_windows()
    {
        Assert.Contains("/SILENT", UpdateService.InstallerArguments);
        Assert.Contains("/NORESTART", UpdateService.InstallerArguments);
    }

    private static UpdateService Updates(FakeRelease network, long installerSize)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network);
        updates.UseRelease(new UpdateService.ReleaseFile("Filee-9.9.9-win-Setup.exe", Base + "Filee-9.9.9-win-Setup.exe", installerSize),
            new UpdateService.ReleaseFile("Filee-9.9.9-SHA256SUMS.txt", Base + "Filee-9.9.9-SHA256SUMS.txt", 100));
        return updates;
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; posts later, which makes the order racy in tests).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>Serves a release's checksum file and installer.</summary>
    private sealed class FakeRelease(byte[] installer, string hash, string sumsName = "Filee-9.9.9-win-Setup.exe",
        HttpStatusCode installerStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int InstallerRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{new string('1', 64)}  Filee-9.9.9-win-Portable.zip\n{hash}  {sumsName}\n", Encoding.ASCII),
                });
            if (url.EndsWith("-win-Setup.exe", StringComparison.Ordinal))
            {
                InstallerRequests++;
                Assert.Contains("application/octet-stream", request.Headers.Accept.ToString());
                return Task.FromResult(new HttpResponseMessage(installerStatus) { Content = new ByteArrayContent(installer) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task A_failed_update_is_reported_instead_of_opening_a_page_right_away()
    {
        // Installed copy, but the release offers no installer to download: the app shows an alert first and opens
        // the website only after OK (WindowService.ShowUpdateFailedAsync).
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeNetwork("no network")) { InstallsItself = true };
        var reported = 0;
        updates.UpdateFailed += (_, _) => reported++;

        await updates.UpdateAsync();

        Assert.Equal(1, reported);
        Assert.Equal(UpdateStep.Failed, updates.Step);
    }

    [AvaloniaFact] // the check publishes its answer on the UI thread
    public async Task Update_installs_the_release_that_is_latest_now_not_the_one_found_earlier()
    {
        // An earlier check found 9.9.9; 9.9.10 came out before Update was pressed.
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new LatestRelease("v9.9.10")) { TargetPlatform = "win" };
        updates.UseRelease(new UpdateService.ReleaseFile("Filee-9.9.9-win-Setup.exe", Base + "Filee-9.9.9-win-Setup.exe", 1),
            new UpdateService.ReleaseFile("Filee-9.9.9-SHA256SUMS.txt", Base + "Filee-9.9.9-SHA256SUMS.txt", 1));
        var announced = 0;
        updates.UpdateFound += (_, _) => announced++;

        Assert.True(await updates.RefreshAsync(announce: false));

        Assert.Equal("Filee-9.9.10-win-Setup.exe", updates.Installer!.Name);
        Assert.Equal("9.9.10", updates.LatestVersion);
        Assert.Equal(0, announced); // no second notice while updating
    }

    /// <summary>GitHub's "latest release" answer for <paramref name="tag"/>, with its installer and checksums.</summary>
    private sealed class LatestRelease(string tag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var version = tag.TrimStart('v');
            var json = $$"""
                {"tag_name":"{{tag}}","assets":[
                  {"name":"Filee-{{version}}-win-Setup.exe","browser_download_url":"{{Base}}Filee-{{version}}-win-Setup.exe","size":1234},
                  {"name":"Filee-{{version}}-SHA256SUMS.txt","browser_download_url":"{{Base}}Filee-{{version}}-SHA256SUMS.txt","size":100}]}
                """;
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public void Download_page_is_the_latest_release()
    {
        Assert.EndsWith("/releases/latest", UpdateService.LatestReleaseUrl);
        Assert.StartsWith(UpdateService.RepositoryUrl.TrimEnd('/'), UpdateService.LatestReleaseUrl);
    }

    [Theory]
    [InlineData(Architecture.X64, "linux-x64")]
    [InlineData(Architecture.Arm64, "linux-arm64")]
    public void Architecture_maps_to_linux_rid(Architecture arch, string expectedRid) =>
        Assert.Equal(expectedRid, UpdateService.GetLinuxRid(arch));

    [Theory]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.S390x)]
    [InlineData(Architecture.Wasm)]
    public void Architecture_unsupported_throws(Architecture arch) =>
        Assert.Throws<PlatformNotSupportedException>(() => UpdateService.GetLinuxRid(arch));

    [Fact]
    public void IsLinuxInstalled_checks_marker_file()
    {
        var folder = TempFolder();
        Assert.False(UpdateService.IsLinuxInstalled(folder));

        var marker = Path.Combine(folder, ".filee-package");
        File.WriteAllText(marker, "com.filee.app\n");
        Assert.True(UpdateService.IsLinuxInstalled(folder));

        File.WriteAllText(marker, "org.other.app");
        Assert.False(UpdateService.IsLinuxInstalled(folder));
    }

    [Fact]
    public void FindFiles_selects_linux_assets_for_specific_rid()
    {
        using var release = JsonDocument.Parse($$"""
            {"tag_name": "v1.2.3", "assets": [
              {"name": "Filee-1.2.3-SHA256SUMS.txt", "size": 100, "browser_download_url": "{{Base}}Filee-1.2.3-SHA256SUMS.txt"},
              {"name": "Filee-1.2.3-win-Setup.exe", "size": 1200, "browser_download_url": "{{Base}}Filee-1.2.3-win-Setup.exe"},
              {"name": "Filee-1.2.3-linux-x64.tar.gz", "size": 2000, "browser_download_url": "{{Base}}Filee-1.2.3-linux-x64.tar.gz"},
              {"name": "Filee-1.2.3-linux-x64-SHA256SUMS.txt", "size": 90, "browser_download_url": "{{Base}}Filee-1.2.3-linux-x64-SHA256SUMS.txt"},
              {"name": "Filee-1.2.3-linux-arm64.tar.gz", "size": 1900, "browser_download_url": "{{Base}}Filee-1.2.3-linux-arm64.tar.gz"},
              {"name": "Filee-1.2.3-linux-arm64-SHA256SUMS.txt", "size": 92, "browser_download_url": "{{Base}}Filee-1.2.3-linux-arm64-SHA256SUMS.txt"}
            ]}
            """);

        var (installerX64, sumsX64) = UpdateService.FindFiles(release.RootElement, "linux-x64");
        Assert.Equal("Filee-1.2.3-linux-x64.tar.gz", installerX64?.Name);
        Assert.Equal("Filee-1.2.3-linux-x64-SHA256SUMS.txt", sumsX64?.Name);

        var (installerArm64, sumsArm64) = UpdateService.FindFiles(release.RootElement, "linux-arm64");
        Assert.Equal("Filee-1.2.3-linux-arm64.tar.gz", installerArm64?.Name);
        Assert.Equal("Filee-1.2.3-linux-arm64-SHA256SUMS.txt", sumsArm64?.Name);

        var (installerWin, sumsWin) = UpdateService.FindFiles(release.RootElement, "win");
        Assert.Equal("Filee-1.2.3-win-Setup.exe", installerWin?.Name);
        Assert.Equal("Filee-1.2.3-SHA256SUMS.txt", sumsWin?.Name);
    }

    [Fact]
    public async Task Checksum_file_for_different_artifact_is_rejected()
    {
        var installerData = RandomNumberGenerator.GetBytes(1000);
        var hash = Sha256(installerData);
        var folder = TempFolder();

        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new FakeLinuxRelease(
            installerData, hash, sumsArtifact: "Filee-1.2.3-linux-arm64.tar.gz"));
        updates.UseRelease(
            new UpdateService.ReleaseFile("Filee-1.2.3-linux-x64.tar.gz", Base + "Filee-1.2.3-linux-x64.tar.gz", installerData.Length),
            new UpdateService.ReleaseFile("Filee-1.2.3-linux-x64-SHA256SUMS.txt", Base + "Filee-1.2.3-linux-x64-SHA256SUMS.txt", 100));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updates.DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExtractLinuxArchive_valid_payload_extracts_and_sets_permissions()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "package.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-1.0.0-linux-x64/Filee", "#!/bin/sh\necho app\n");
            AddFileEntry(writer, "Filee-1.0.0-linux-x64/install.sh", "#!/bin/sh\necho installer\n");
            AddFileEntry(writer, "Filee-1.0.0-linux-x64/filee.png", "fake png data");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        var payloadDir = UpdateService.ExtractLinuxArchive(archiveFile, destFolder);

        Assert.Equal(Path.Combine(destFolder, "Filee-1.0.0-linux-x64"), payloadDir);
        var installSh = Path.Combine(payloadDir, "install.sh");
        var fileeExe = Path.Combine(payloadDir, "Filee");
        Assert.True(File.Exists(installSh));
        Assert.True(File.Exists(fileeExe));

        if (OperatingSystem.IsLinux())
        {
            var installMode = File.GetUnixFileMode(installSh);
            Assert.True((installMode & UnixFileMode.UserExecute) != 0, "install.sh must be executable");
            var fileeMode = File.GetUnixFileMode(fileeExe);
            Assert.True((fileeMode & UnixFileMode.UserExecute) != 0, "Filee must be executable");
        }
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_path_traversal()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "traversal.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "../evil.sh", "#!/bin/sh\n");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_absolute_path()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "absolute.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "/tmp/evil.sh", "#!/bin/sh\n");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_escaping_symlink()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "symlink.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddSymlinkEntry(writer, "Filee-1.0.0/escape_link", "/etc/passwd");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_missing_payload_files()
    {
        var folder = TempFolder();
        var archiveFile = Path.Combine(folder, "missing.tar.gz");
        var destFolder = Path.Combine(folder, "extracted");

        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-1.0.0/filee.png", "png data");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [AvaloniaFact]
    public async Task UpdateAsync_on_linux_downloads_extracts_and_invokes_installer_with_upgrade_and_restart()
    {
        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/Filee", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/install.sh", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/filee.png", "png data");
        });
        var hash = Sha256(tarBytes);
        var network = new FakeLinuxRelease(tarBytes, hash, "Filee-9.9.9-linux-x64.tar.gz", "9.9.9");

        var mockInstallDir = TempFolder();
        File.WriteAllText(Path.Combine(mockInstallDir, ".filee-version"), "9.9.9");

        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network)
        {
            InstallsItself = true,
            TargetPlatform = "linux-x64",
            LinuxInstallDirectoryOverride = mockInstallDir,
        };
        updates.UseRelease(
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64.tar.gz", Base + "Filee-9.9.9-linux-x64.tar.gz", tarBytes.Length),
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64-SHA256SUMS.txt", Base + "Filee-9.9.9-linux-x64-SHA256SUMS.txt", 100));

        var processes = new List<ProcessStartInfo>();
        var shutdownRequested = false;
        updates.RequestApplicationShutdown = () => shutdownRequested = true;
        updates.StartProcess = psi =>
        {
            processes.Add(psi);
            return Process.Start(new ProcessStartInfo("true") { UseShellExecute = false });
        };

        await updates.UpdateAsync();

        Assert.Equal(UpdateStep.Idle, updates.Step);
        Assert.True(processes.Count >= 2);

        var installerPsi = processes[0];
        Assert.Equal("/bin/sh", installerPsi.FileName);
        Assert.Equal(3, installerPsi.ArgumentList.Count);
        Assert.EndsWith("install.sh", installerPsi.ArgumentList[0]);
        Assert.Equal("--upgrade", installerPsi.ArgumentList[1]);
        Assert.Equal("--restart", installerPsi.ArgumentList[2]);
        Assert.Equal("1", installerPsi.Environment["FILEE_IN_APP_UPDATE"]);
        Assert.Equal(installerPsi.WorkingDirectory, Path.GetDirectoryName(installerPsi.ArgumentList[0]));

        var restartPsi = processes[1];
        Assert.Equal("/bin/sh", restartPsi.FileName);
        Assert.Equal("-c", restartPsi.ArgumentList[0]);
        Assert.Contains("kill -0", restartPsi.ArgumentList[1]);
        Assert.Equal(Environment.ProcessId.ToString(), restartPsi.ArgumentList[3]);
        Assert.Equal(Path.Combine(mockInstallDir, "Filee"), restartPsi.ArgumentList[4]);
        Assert.Equal(mockInstallDir, restartPsi.WorkingDirectory);
        Assert.False(restartPsi.Environment.ContainsKey("FILEE_IN_APP_UPDATE"));

        Assert.True(shutdownRequested, "Application shutdown must be requested after verified installation");
    }

    [AvaloniaFact]
    public async Task UpdateAsync_on_linux_handles_installer_failure()
    {
        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/Filee", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/install.sh", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/filee.png", "png data");
        });
        var hash = Sha256(tarBytes);
        var network = new FakeLinuxRelease(tarBytes, hash, "Filee-9.9.9-linux-x64.tar.gz", "9.9.9");

        var mockInstallDir = TempFolder();
        var backupDir = Path.Combine(Path.GetDirectoryName(mockInstallDir)!, ".backup-123");
        Directory.CreateDirectory(backupDir);

        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network)
        {
            InstallsItself = true,
            TargetPlatform = "linux-x64",
            LinuxInstallDirectoryOverride = mockInstallDir,
        };
        updates.UseRelease(
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64.tar.gz", Base + "Filee-9.9.9-linux-x64.tar.gz", tarBytes.Length),
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64-SHA256SUMS.txt", Base + "Filee-9.9.9-linux-x64-SHA256SUMS.txt", 100));

        var failedCount = 0;
        updates.UpdateFailed += (_, _) => failedCount++;

        var shutdownRequested = false;
        updates.RequestApplicationShutdown = () => shutdownRequested = true;

        updates.StartProcess = psi =>
            Process.Start(new ProcessStartInfo("false") { UseShellExecute = false });

        await updates.UpdateAsync();

        Assert.Equal(UpdateStep.Failed, updates.Step);
        Assert.Equal(1, failedCount);
        Assert.NotNull(updates.LastError);
        Assert.Contains("Installer ended with exit code 1", updates.LastError.Message);
        Assert.Contains("mv '" + backupDir + "' '" + mockInstallDir + "'", updates.LastError.Message);
        Assert.False(shutdownRequested, "Application shutdown must not be requested when installer fails");
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_symlink_component_traversal()
    {
        var outsideFolder = TempFolder();
        var sentinelFile = Path.Combine(outsideFolder, "sentinel.txt");
        File.WriteAllText(sentinelFile, "SENTINEL_UNTOUCHED");

        var stagingFolder = TempFolder();
        var archiveFile = Path.Combine(stagingFolder, "evil-traversal.tar.gz");
        var destFolder = Path.Combine(stagingFolder, "dest");

        var tarBytes = CreateTarGz(writer =>
        {
            // Adversarial entry 1: directory symlink pointing outside to outsideFolder
            AddSymlinkEntry(writer, "payload/escape", outsideFolder);
            // Adversarial entry 2: file inside escape pointing to sentinel.txt
            AddFileEntry(writer, "payload/escape/sentinel.txt", "MALICIOUS_OVERWRITE");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
        Assert.Equal("SENTINEL_UNTOUCHED", File.ReadAllText(sentinelFile));
    }

    [Fact]
    public void ExtractLinuxArchive_rejects_link_order_attack()
    {
        var stagingFolder = TempFolder();
        var archiveFile = Path.Combine(stagingFolder, "evil-link-order.tar.gz");
        var destFolder = Path.Combine(stagingFolder, "dest");

        var tarBytes = CreateTarGz(writer =>
        {
            // Entry 1: regular directory
            var dirEntry = new PaxTarEntry(TarEntryType.Directory, "payload/sub");
            writer.WriteEntry(dirEntry);
            // Entry 2: attempt to replace directory with a symlink
            AddSymlinkEntry(writer, "payload/sub", "other");
        });
        File.WriteAllBytes(archiveFile, tarBytes);

        Assert.Throws<InvalidDataException>(() => UpdateService.ExtractLinuxArchive(archiveFile, destFolder));
    }

    [Fact]
    public void CreateSecureWorkspace_creates_private_unshared_directory()
    {
        var workspace = UpdateService.CreateSecureWorkspace();
        try
        {
            Assert.True(Directory.Exists(workspace));
            var dirInfo = new DirectoryInfo(workspace);
            Assert.Null(dirInfo.LinkTarget);
            Assert.False(dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint));

            if (OperatingSystem.IsLinux())
            {
                var mode = File.GetUnixFileMode(workspace);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
            }
        }
        finally
        {
            UpdateService.DeleteSecureWorkspace(workspace);
            Assert.False(Directory.Exists(workspace));
        }
    }

    [Fact]
    public void DeleteSecureWorkspace_refuses_to_follow_symlink()
    {
        var realDir = TempFolder();
        var targetFile = Path.Combine(realDir, "important.txt");
        File.WriteAllText(targetFile, "CRITICAL_DATA");

        var linkContainer = TempFolder();
        var symlinkPath = Path.Combine(linkContainer, "link-to-real");
        File.CreateSymbolicLink(symlinkPath, realDir);

        UpdateService.DeleteSecureWorkspace(symlinkPath);

        Assert.True(Directory.Exists(realDir), "Real directory must not be deleted when DeleteSecureWorkspace is called on a symlink");
        Assert.True(File.Exists(targetFile), "File inside real directory must survive");
    }

    [Fact]
    public async Task Concurrent_secure_workspaces_are_isolated()
    {
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(() => UpdateService.CreateSecureWorkspace())).ToArray();
        var workspaces = await Task.WhenAll(tasks);

        try
        {
            Assert.Equal(5, workspaces.Distinct().Count());
            Assert.All(workspaces, w => Assert.True(Directory.Exists(w)));
        }
        finally
        {
            foreach (var w in workspaces)
                UpdateService.DeleteSecureWorkspace(w);
        }
    }

    [Fact]
    public void VerifyInstalledVersion_detects_mismatch()
    {
        var folder = TempFolder();
        File.WriteAllText(Path.Combine(folder, ".filee-version"), "1.0.0");

        Assert.True(UpdateService.VerifyInstalledVersion(folder, "1.0.0", out var actual));
        Assert.Equal("1.0.0", actual);

        Assert.False(UpdateService.VerifyInstalledVersion(folder, "1.0.2", out actual));
        Assert.Equal("1.0.0", actual);

        var emptyFolder = TempFolder();
        Assert.False(UpdateService.VerifyInstalledVersion(emptyFolder, "1.0.0", out actual));
        Assert.Null(actual);
    }

    [AvaloniaFact]
    public async Task UpdateAsync_on_linux_fails_when_installed_version_does_not_match()
    {
        var tarBytes = CreateTarGz(writer =>
        {
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/Filee", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/install.sh", "#!/bin/sh\nexit 0\n");
            AddFileEntry(writer, "Filee-9.9.9-linux-x64/filee.png", "png data");
        });
        var hash = Sha256(tarBytes);
        var network = new FakeLinuxRelease(tarBytes, hash, "Filee-9.9.9-linux-x64.tar.gz", "9.9.9");

        var mockInstallDir = TempFolder();
        File.WriteAllText(Path.Combine(mockInstallDir, ".filee-version"), "1.0.0"); // Mismatch: expecting 9.9.9

        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network)
        {
            InstallsItself = true,
            TargetPlatform = "linux-x64",
            LinuxInstallDirectoryOverride = mockInstallDir,
        };
        updates.UseRelease(
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64.tar.gz", Base + "Filee-9.9.9-linux-x64.tar.gz", tarBytes.Length),
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64-SHA256SUMS.txt", Base + "Filee-9.9.9-linux-x64-SHA256SUMS.txt", 100));

        var failedCount = 0;
        updates.UpdateFailed += (_, _) => failedCount++;

        var shutdownRequested = false;
        updates.RequestApplicationShutdown = () => shutdownRequested = true;

        updates.StartProcess = psi =>
            Process.Start(new ProcessStartInfo("true") { UseShellExecute = false });

        await updates.UpdateAsync();

        Assert.Equal(UpdateStep.Failed, updates.Step);
        Assert.Equal(1, failedCount);
        Assert.NotNull(updates.LastError);
        Assert.Contains("Update verification failed", updates.LastError.Message);
        Assert.False(shutdownRequested, "Application shutdown must not be requested when version verification fails");
    }

    private static byte[] CreateTarGz(Action<TarWriter> populate)
    {
        using var mem = new MemoryStream();
        using (var gzip = new GZipStream(mem, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            populate(writer);
        }
        return mem.ToArray();
    }

    private static void AddFileEntry(TarWriter writer, string name, string content, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
            Mode = mode,
        };
        writer.WriteEntry(entry);
    }

    private static void AddSymlinkEntry(TarWriter writer, string name, string target)
    {
        var entry = new PaxTarEntry(TarEntryType.SymbolicLink, name)
        {
            LinkName = target,
        };
        writer.WriteEntry(entry);
    }

    private sealed class FakeLinuxRelease(byte[] archive, string hash, string sumsArtifact = "Filee-9.9.9-linux-x64.tar.gz", string version = "9.9.9") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                var json = $$"""
                    {"tag_name":"v{{version}}","assets":[
                      {"name":"Filee-{{version}}-linux-x64.tar.gz","browser_download_url":"{{Base}}Filee-{{version}}-linux-x64.tar.gz","size":{{archive.Length}}},
                      {"name":"Filee-{{version}}-linux-x64-SHA256SUMS.txt","browser_download_url":"{{Base}}Filee-{{version}}-linux-x64-SHA256SUMS.txt","size":100}]}
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }
            if (url.Contains("/releases?per_page=", StringComparison.Ordinal) || url.EndsWith("/releases", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                });
            }
            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{hash}  {sumsArtifact}\n", Encoding.ASCII),
                });
            }
            if (url.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                Assert.Contains("application/octet-stream", request.Headers.Accept.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
