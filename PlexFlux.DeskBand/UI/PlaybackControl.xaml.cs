﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Xml;
using PlexFlux.IPC;
using CSDeskBand;
using System.Runtime.InteropServices;

namespace PlexFlux.DeskBand.UI
{
    /// <summary>
    /// Interaction logic for PlaybackControl.xaml
    /// </summary>
    public partial class PlaybackControl : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(PlaybackControl));

        public static readonly DependencyProperty PositionProperty =
            DependencyProperty.Register("Position", typeof(long), typeof(PlaybackControl));

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.Register("Duration", typeof(long), typeof(PlaybackControl));

        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register("IsPlaying", typeof(bool), typeof(PlaybackControl));

        public string Title
        {
            get
            {
                return (string)GetValue(TitleProperty);
            }
            private set
            {
                SetValue(TitleProperty, value);
            }
        }

        public long Position
        {
            get
            {
                return (long)GetValue(PositionProperty);
            }
            private set
            {
                SetValue(PositionProperty, value);
            }
        }

        public long Duration
        {
            get
            {
                return (long)GetValue(DurationProperty);
            }
            private set
            {
                SetValue(DurationProperty, value);
            }
        }

        public bool IsPlaying
        {
            get
            {
                return (bool)GetValue(IsPlayingProperty);
            }
            private set
            {
                SetValue(IsPlayingProperty, value);
            }
        }

        private TaskScheduler uiContext;
        private bool doNotTriggerVolume;

        public PlaybackControl()
        {
            uiContext = TaskScheduler.FromCurrentSynchronizationContext();
            Visibility = Visibility.Hidden;
            
            InitializeComponent();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            var client = IPCClient.GetInstance();
            client.Disconnected += IPCClient_Disconnected;
            client.MessageReceived += IPCClient_MessageReceived;

            client.Init();
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            var client = IPCClient.GetInstance();
            client.Disconnected -= IPCClient_Disconnected;
            client.MessageReceived -= IPCClient_MessageReceived;

            client.Close();
        }

        private void IPCClient_Disconnected(object sender, EventArgs e)
        {
            Task.Factory.StartNew(() =>
            {
                Visibility = Visibility.Hidden;

                var deskband = DeskBand.GetInstance();
                deskband.ShowDeskBand(false);

            }, CancellationToken.None, TaskCreationOptions.None, uiContext);
        }

        private void IPCClient_MessageReceived(object sender, XmlNode messageNode)
        {
            Task.Factory.StartNew(() =>
            {
                Visibility = Visibility.Visible;

                var deskband = DeskBand.GetInstance();
                deskband.ShowDeskBand(true);

                switch (messageNode.Attributes["action"].InnerText)
                {
                    case "playbackStateChanged":
                        var hasTrack = messageNode.SelectSingleNode("hasTrack").InnerText == "true";

                        if (hasTrack)
                        {
                            Title = messageNode.SelectSingleNode("title").InnerText;
                            Duration = long.Parse(messageNode.SelectSingleNode("duration").InnerText);
                            IsPlaying = messageNode.SelectSingleNode("playing").InnerText == "true";
                            Position = long.Parse(messageNode.SelectSingleNode("position").InnerText);

                            // load artwork
                            BitmapImage bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.UriSource = new Uri(messageNode.SelectSingleNode("artwork").InnerText, UriKind.Absolute);
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.EndInit();

                            imageArtwork.Source = bitmap;
                        }
                        else
                        {
                            Title = string.Empty;
                            Duration = 1;
                            IsPlaying = false;
                            Position = 0;

                            // unload artwork
                            imageArtwork.Source = null;
                        }

                        buttonPlay.Visibility = IsPlaying ? Visibility.Collapsed : Visibility.Visible;
                        buttonPause.Visibility = IsPlaying ? Visibility.Visible : Visibility.Collapsed;
                        break;

                    case "positionChanged":
                        Position = long.Parse(messageNode.SelectSingleNode("position").InnerText);
                        break;

                    case "volumeChanged":
                        doNotTriggerVolume = true;
                        sliderVolume.Value = int.Parse(messageNode.SelectSingleNode("volume").InnerText);
                        break;
                }

            }, CancellationToken.None, TaskCreationOptions.None, uiContext);
        }

        private void textTitle_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // stop animation
            textTitle.BeginAnimation(Canvas.LeftProperty, null);

            if (textTitle.ActualWidth > panelTitle.ActualWidth)
            {
                textTitle.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation
                {
                    From = panelTitle.ActualWidth,
                    To = -textTitle.ActualWidth,
                    RepeatBehavior = RepeatBehavior.Forever,
                    FillBehavior = FillBehavior.Stop,
                    Duration = new Duration(TimeSpan.FromSeconds(textTitle.ActualWidth / 15))
                });
            }
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            var client = IPCClient.GetInstance();
            var factory = new IPCMessageFactory();
            client.Send(factory.Create("previous"));
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            var client = IPCClient.GetInstance();
            var factory = new IPCMessageFactory();
            client.Send(factory.Create("next"));
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            var client = IPCClient.GetInstance();
            var factory = new IPCMessageFactory();
            client.Send(factory.Create("play"));
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            var client = IPCClient.GetInstance();
            var factory = new IPCMessageFactory();
            client.Send(factory.Create("pause"));
        }

        private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (doNotTriggerVolume)
            {
                doNotTriggerVolume = false;
                return;
            }

            var factory = new IPCMessageFactory();
            var message = factory.Create("setVolume", out XmlNode messageNode);

            var volumeNode = message.CreateElement("volume");
            volumeNode.InnerText = ((int)e.NewValue).ToString();
            messageNode.AppendChild(volumeNode);

            var client = IPCClient.GetInstance();
            client.Send(message);
        }
    }
}
