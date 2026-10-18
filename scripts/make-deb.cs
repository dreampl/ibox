// Builds a Debian package (and a .tar.gz) on Windows with correct Unix permissions.
// dotnet run scripts/make-deb.cs -- <staging-root> <version> <out-dir>
// <staging-root> holds the package tree (opt/jackivpn/...). Files without an extension are executables.
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

var root = Path.GetFullPath(args[0]);
var version = args[1];
var outDir = Path.GetFullPath(args[2]);
Directory.CreateDirectory(outDir);
var mtime = DateTimeOffset.UtcNow;
const UnixFileMode Dir = (UnixFileMode)0b111_101_101;
const UnixFileMode Exe = (UnixFileMode)0b111_101_101;
const UnixFileMode Reg = (UnixFileMode)0b110_100_100;

byte[] DataTar()
{
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var tar = new TarWriter(gz, TarEntryFormat.Gnu))
    {
        foreach (var dir in new[] { "./opt/", "./usr/", "./usr/bin/" }.Concat(Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                     .Select(d => "./" + Path.GetRelativePath(root, d).Replace('\\', '/') + "/").Where(d => d != "./opt/").OrderBy(d => d, StringComparer.Ordinal)).Distinct())
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, dir) { Mode = Dir, ModificationTime = mtime });
        }

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = "./" + Path.GetRelativePath(root, file).Replace('\\', '/');
            var entry = new GnuTarEntry(TarEntryType.RegularFile, name)
            {
                Mode = Path.HasExtension(file) ? Reg : Exe,
                ModificationTime = mtime,
                DataStream = File.OpenRead(file),
            };
            tar.WriteEntry(entry);
            entry.DataStream.Dispose();
        }

        tar.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, "./usr/bin/jackivpn") { LinkName = "/opt/jackivpn/jackivpn", Mode = Exe, ModificationTime = mtime });
        // The command people type: ibox (jackivpn stays as an older name).
        tar.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, "./usr/bin/ibox") { LinkName = "/opt/jackivpn/jackivpn", Mode = Exe, ModificationTime = mtime });

        // Desktop menu entry and icon.
        foreach (var dir in new[] { "./usr/share/", "./usr/share/applications/", "./usr/share/icons/", "./usr/share/icons/hicolor/", "./usr/share/icons/hicolor/scalable/", "./usr/share/icons/hicolor/scalable/apps/" })
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, dir) { Mode = Dir, ModificationTime = mtime });
        }

        const string desktop = "[Desktop Entry]\nType=Application\nName=IBOX\nComment=IBOX VPN client\nExec=ibox gui\nIcon=jackivpn\nTerminal=false\nCategories=Network;\nStartupWMClass=jackivpn\n";
        const string icon = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024"><rect width="1024" height="1024" rx="226" fill="#000000"/><g transform="translate(102.4 410.992) scale(1.59068)"><g fill="#FFFFFF"><rect x="1" y="0" width="12" height="127"/><rect x="19" y="0" width="10" height="127"/><rect x="38" y="0" width="7" height="127"/><rect x="62" y="0" width="6" height="127"/><path fill-rule="evenodd" d="M96 0H176Q190 0 190 14V50H204Q218 50 218 64V97Q218 127 188 127H96ZM123 27H163V50H123ZM123 73H191V100H123Z"/><path d="M272 0H368V127H250Q242 127 242 119V30H269V100H341V27H272Z"/><path d="M416 0H448L514 127H482Z"/><path d="M376 127H408L511 0H479Z"/></g><rect x="309" y="53" width="22" height="22" fill="#3D9BFF"/></g></svg>""";
        tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./usr/share/applications/jackivpn.desktop") { Mode = Reg, ModificationTime = mtime, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(desktop)) });
        tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./usr/share/icons/hicolor/scalable/apps/jackivpn.svg") { Mode = Reg, ModificationTime = mtime, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(icon)) });

        // The one password prompt of a session (the privileged helper, started with pkexec when the app opens) says
        // what it is for; it is asked again only when the app is opened again.
        foreach (var dir in new[] { "./usr/share/polkit-1/", "./usr/share/polkit-1/actions/" })
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, dir) { Mode = Dir, ModificationTime = mtime });
        }

        const string policy = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE policyconfig PUBLIC "-//freedesktop//DTD PolicyKit Policy Configuration 1.0//EN" "http://www.freedesktop.org/standards/PolicyKit/1/policyconfig.dtd">
            <policyconfig>
              <vendor>IBOX</vendor>
              <action id="com.jacki.ibox.helper">
                <description>Run IBOX's network helper for this session</description>
                <message>IBOX needs your permission once for this session to route all traffic through the VPN (whole-system mode).</message>
                <defaults>
                  <allow_any>auth_admin</allow_any>
                  <allow_inactive>auth_admin</allow_inactive>
                  <allow_active>auth_admin</allow_active>
                </defaults>
                <annotate key="org.freedesktop.policykit.exec.path">/opt/jackivpn/jackivpn</annotate>
                <annotate key="org.freedesktop.policykit.exec.argv1">helper</annotate>
              </action>
            </policyconfig>
            """;
        tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./usr/share/polkit-1/actions/com.jacki.ibox.helper.policy") { Mode = Reg, ModificationTime = mtime, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(policy.Replace("\r\n", "\n", StringComparison.Ordinal))) });
    }

    return ms.ToArray();
}

byte[] ControlTar(long installedKb)
{
    var control = $"""
        Package: jackivpn
        Version: {version}
        Architecture: amd64
        Maintainer: Jacki <support@jackiairline.ir>
        Installed-Size: {installedKb}
        Depends: libc6 (>= 2.31), ca-certificates, libcap2-bin, libicu74 | libicu72 | libicu70 | libicu67 | libicu66, libx11-6, libice6, libsm6, libfontconfig1
        Section: net
        Priority: optional
        Homepage: https://connect.jackiairline.ir
        Description: IBOX client (desktop window and command line)
         sing-box + Xray VPN client with PasarGuard subscriptions, smart server
         selection, proxy mode (GNOME proxy) and whole-system TUN mode.

        """.Replace("\r\n", "\n");
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var tar = new TarWriter(gz, TarEntryFormat.Gnu))
    {
        tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./") { Mode = Dir, ModificationTime = mtime });
        tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./control") { Mode = Reg, ModificationTime = mtime, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(control)) });

        // Whole-system (TUN) mode without sudo: only sing-box gets the network-admin capability, not the app.
        // The tunnel sets its DNS through systemd-resolved, which asked for the password on every connect: the
        // installing user joins the jackivpn group, which a polkit rule lets do that (same files as the app's
        // one-time setup in LinuxTunAccess).
        var postinst = """
            #!/bin/sh
            set -e
            if command -v setcap >/dev/null 2>&1; then
              setcap cap_net_admin,cap_net_raw,cap_net_bind_service+ep /opt/jackivpn/cores/sing-box || true
            fi
            getent group jackivpn >/dev/null || groupadd -r jackivpn || true
            for u in "${SUDO_USER:-}" "$(id -nu "${PKEXEC_UID:-x}" 2>/dev/null || true)"; do
              if [ -n "$u" ] && [ "$u" != root ] && id "$u" >/dev/null 2>&1; then usermod -aG jackivpn "$u" || true; fi
            done
            mkdir -p /etc/polkit-1/rules.d
            cat > /etc/polkit-1/rules.d/49-jackivpn.rules <<'EOF'
            // IBOX: the VPN tunnel sets its own DNS (systemd-resolved) without asking for the password every time.
            polkit.addRule(function(action, subject) {
                if ((action.id.indexOf("org.freedesktop.resolve1.set-") == 0 || action.id == "org.freedesktop.resolve1.revert")
                    && subject.local && subject.isInGroup("jackivpn")) {
                    return polkit.Result.YES;
                }
            });
            EOF
            chmod 644 /etc/polkit-1/rules.d/49-jackivpn.rules
            if [ -d /etc/polkit-1/localauthority ]; then
              mkdir -p /etc/polkit-1/localauthority/50-local.d
              cat > /etc/polkit-1/localauthority/50-local.d/49-jackivpn.pkla <<'EOF'
            [IBOX tunnel DNS]
            Identity=unix-group:jackivpn
            Action=org.freedesktop.resolve1.set-dns-servers;org.freedesktop.resolve1.set-domains;org.freedesktop.resolve1.set-default-route;org.freedesktop.resolve1.set-llmnr;org.freedesktop.resolve1.set-mdns;org.freedesktop.resolve1.set-dns-over-tls;org.freedesktop.resolve1.set-dnssec;org.freedesktop.resolve1.set-dnssec-negative-trust-anchors;org.freedesktop.resolve1.revert
            ResultAny=yes
            ResultInactive=yes
            ResultActive=yes
            EOF
            fi
            exit 0

            """.Replace("\r\n", "\n");
        const string postrm = "#!/bin/sh\nset -e\nif [ \"$1\" = remove ] || [ \"$1\" = purge ]; then\n  rm -f /etc/polkit-1/rules.d/49-jackivpn.rules /etc/polkit-1/localauthority/50-local.d/49-jackivpn.pkla\nfi\nexit 0\n";
        tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./postinst") { Mode = Exe, ModificationTime = mtime, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(postinst)) });
        tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./postrm") { Mode = Exe, ModificationTime = mtime, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(postrm)) });
    }

    return ms.ToArray();
}

void ArMember(Stream s, string name, byte[] data)
{
    var header = $"{name,-16}{mtime.ToUnixTimeSeconds(),-12}{0,-6}{0,-6}{"100644",-8}{data.Length,-10}`\n";
    s.Write(Encoding.ASCII.GetBytes(header));
    s.Write(data);
    if (data.Length % 2 == 1)
    {
        s.WriteByte((byte)'\n');
    }
}

var data = DataTar();
var size = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
var deb = Path.Combine(outDir, $"ibox_{version}_amd64.deb");
using (var fs = File.Create(deb))
{
    fs.Write("!<arch>\n"u8);
    ArMember(fs, "debian-binary", "2.0\n"u8.ToArray());
    ArMember(fs, "control.tar.gz", ControlTar(size));
    ArMember(fs, "data.tar.gz", data);
}

// The same tree as a plain archive (other distributions: extract to / or run from anywhere).
File.WriteAllBytes(Path.Combine(outDir, $"ibox-{version}-linux-x64.tar.gz"), data);
Console.WriteLine(deb);
