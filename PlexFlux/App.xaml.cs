﻿using System;
using System.Threading.Tasks;
using System.Windows;
using System.Reflection;
using System.Net;
using System.Threading;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Xml;
using System.IO;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using PlexLib;
using PlexFlux.UI;
using Octokit;

namespace PlexFlux
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static readonly Mutex appMutex = new Mutex(true,
#if DEBUG
            "{FF3ADB00-7905-4488-90C8-79AB16DC47DC}"
#else
            "{14B7E89A-58A5-48EE-99BD-BAB4CF5C20AF}"
#endif
            );

        public AppConfig config;
        public PlexDeviceInfo deviceInfo;

        public MyPlexClient myPlexClient;
        public PlexConnection plexConnection;
        public PlexClient plexClient;

        public TaskScheduler uiContext;

        private static List<string> untrustedCertificates = new List<string>();

        protected override void OnStartup(StartupEventArgs e)
        {
            ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(CertificateValidationError_Callback);

            if (!appMutex.WaitOne(TimeSpan.Zero, true))
            {
                MessageBox.Show("PlexFlux is already running.\nOnly one instance at a time.", "PlexFlux", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                Environment.Exit(128);
                return;
            }

            // As we are using WASAPI, we are not compatible with Vista below. (no point to support shit legacy OS)
            if (Environment.OSVersion.Version.Major < 6)    // vista is 6.0
            {
                MessageBox.Show("PlexFlux is not compatible with your current version of Windows.\nWindows Vista or later is required.", "PlexFlux", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(128);
                return;
            }

#if DEBUG
            while (!System.Diagnostics.Debugger.IsAttached)
            {
                Thread.Sleep(100);
            }
#endif

            base.OnStartup(e);
            uiContext = TaskScheduler.FromCurrentSynchronizationContext();

            // app config
            try
            {
                config = AppConfig.Load();
            }
            catch (Exception)
            {
                config = new AppConfig();
            }

            // init plex
            deviceInfo = new PlexDeviceInfo("PlexFlux", Assembly.GetExecutingAssembly().GetName().Version.ToString(), config.ClientIdentifier);
            myPlexClient = new MyPlexClient(deviceInfo, config.AuthenticationToken.Length == 0 ? null : config.AuthenticationToken);
            plexConnection = null;
            plexClient = null;

            // init IPC
            var ipc = IPCManager.GetInstance();
            ipc.Init();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            config.Save();

            // reset playback manager to dispose StreamingWaveProvider because we want to make sure the temp file generated by StreamingWaveProvider is deleted
            var playback = PlaybackManager.GetInstance();
            playback.Stop();
            playback.Reset();

            //base.OnExit(e);
            Environment.Exit(e.ApplicationExitCode);
        }

        private bool CertificateValidationError_Callback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // accept no error
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            // for security, we do not accept anything that is unknown
            if (!(sender is WebRequest))
                return false;

            var request = sender as WebRequest;

            // we do not want to send our username and password to hacker
            if (request.RequestUri.Host == "plex.tv" || request.RequestUri.Host == "app.plex.tv")
                return false;

            var hash = certificate.GetCertHashString();

            if (untrustedCertificates.Contains(hash))
                return false;

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
#if DEBUG
                "PlexFlux_d"
#else
                "PlexFlux"
#endif
                , "trusted_certificates.xml");

            XmlDocument xml = new XmlDocument();

            try
            {
                xml.LoadXml(File.ReadAllText(path));
            }
            catch
            {
                // File is not created or cannot be accessed
                var declaration = xml.CreateXmlDeclaration("1.0", "utf-8", null);
                xml.AppendChild(declaration);

                var rootNode = xml.CreateElement("trusted");
                xml.AppendChild(rootNode);
            }

            var trustedNode = xml.SelectSingleNode("/trusted");

            foreach (XmlNode certificateNode in trustedNode.SelectNodes("certificate"))
            {
                if (certificateNode.InnerText == hash)
                    return true;
            }

            // not listed in trusted certificate
            if (MessageBox.Show("Do you want to trust the following SSL certificate?\n\n" +
                "[Connecting Host]\n" +
                request.RequestUri.Host + "\n\n" +
                certificate.ToString() + "\n" +
                "--- WARNING ---\nIf you are not sure, you should not trust any certificate as your information can be evaspdropped by hacker.", "SSL Policy", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                lock (untrustedCertificates)
                    untrustedCertificates.Add(hash);

                plexConnection = null;
                plexClient = null;

                return false;
            }

            var certNode = xml.CreateElement("certificate");
            certNode.InnerText = hash;

            trustedNode.AppendChild(certNode);

            // save it to path
            var dir = Path.GetDirectoryName(path);

            if (Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var file = File.Open(path, System.IO.FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
                xml.Save(file);

            return true;
        }

        public async Task<PlexServer[]> GetPlexServers()
        {
            PlexServer[] servers = null;

            while (servers == null)
            {
                try
                {
                    servers = await myPlexClient.GetServers();
                }
                catch (WebException)
                {
                    AuthWindow authWindow = new AuthWindow();

                    if (authWindow.ShowDialog() == false)
                        Environment.Exit(0);

                    // update app config
                    config.AuthenticationToken = myPlexClient.AuthenticationToken;
                    config.Save();
                }
                catch (Exception e)
                {
                    MessageBox.Show("An unknown error has occurred.\nPlexFlux will now quit.\n\n" + e.Message, "PlexFlux", MessageBoxButton.OK, MessageBoxImage.Error);
                    Environment.Exit(128);
                }
            }

            return servers;
        }

        public PlexServer SelectPlexServer()
        {
            var serverSelectionWindow = new ServerSelectionWindow();
            serverSelectionWindow.ShowDialog();

            var server = serverSelectionWindow.SelectedPlexServer;

            // save to app config
            if (server != null)
            {
                config.ServerMachineIdentifier = server.MachineIdentifier;
                config.LastPlaylist = null;
                config.Save();
            }

            return server;
        }

        public Task ConnectToPlexServer(PlexServer server)
        {
            return Task.Factory.StartNew(() =>
            {
                if (!server.HasSelectedAddress)
                    server.SelectAddress(); // this, should not block the UI

                plexConnection = new PlexConnection(deviceInfo, server);
                plexClient = new PlexClient(plexConnection);

            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public MMDevice GetDeviceByID(string deviceID)
        {
            var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (device.ID == deviceID)
                    return device;
            }

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }

        public async Task<Version> GetLatestVersion()
        {
            var github = new GitHubClient(new ProductHeaderValue("PlexFlux"));

            Version latestVersion = null;
            Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;

            try
            {
                var release = await github.Repository.Release.GetLatest("brian9206", "PlexFlux");
                latestVersion = Version.Parse(release.TagName);
            }
            catch
            {
                // internet connection issue or no releases
                return currentVersion;
            }

            return latestVersion;
        }
    }
}
