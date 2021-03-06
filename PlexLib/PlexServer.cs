﻿using System;
using System.Threading.Tasks;
using System.Net;
using System.IO;
using System.Xml;
using System.Collections.Generic;

namespace PlexLib
{
    public class PlexServer
    {
        public string Url
        {
            get;
            private set;
        }

        public string Name
        {
            get;
            internal set;
        }

        public string Address
        {
            get;
            internal set;
        }

        public string[] LocalAddressList
        {
            get;
            internal set;
        }

        public string AccessToken
        {
            get;
            internal set;
        }

        public string MachineIdentifier
        {
            get;
            internal set;
        }

        public bool HasSelectedAddress
        {
            get
            {
                return Url != null;
            }
        }

        internal PlexServer()
        {
            Url = null;
        }

        public bool SelectAddress()
        {
            // get fastest connection in local address list
            var tasks = new Task<bool>[LocalAddressList.Length];
            var requests = new HttpWebRequest[LocalAddressList.Length];

            for (int i = 0; i < LocalAddressList.Length; i++)
            {
                int idx = i;
                tasks[idx] = Task.Run(() =>
                {
                    requests[idx] = CreateConnectivityRequest(LocalAddressList[idx], true);
                    return CheckConnectivity(requests[idx]);
                });
            }
                
            int index = Task.WaitAny(tasks);

            if (tasks[index].Result)
            {
                Url = LocalAddressList[index];

                // abort all connections
                foreach (var request in requests)
                {
                    try
                    {
                        request.Abort();
                    }
                    catch (Exception)
                    {
                    }
                }
                    
            }
            else
            {
                var request = CreateConnectivityRequest(Address);

                // no host available
                if (!CheckConnectivity(request))
                    return false;

                Url = Address;
            }

            return true;
        }

        private bool CheckConnectivity(HttpWebRequest request)
        {
            if (request == null)
                return false;

            try
            {
                var response = request.GetResponse();
                var streamReader = new StreamReader(response.GetResponseStream());

                var xml = new XmlDocument();
                xml.LoadXml(streamReader.ReadToEnd());

                var serverInfo = xml.SelectSingleNode("/MediaContainer");
                if (serverInfo.Attributes["machineIdentifier"].InnerText != MachineIdentifier)
                    throw new WebException("MachineIdentifier is not match.");

                return true;
            }
            catch (Exception)
            {
                //System.Threading.Thread.Sleep(5000);
                return false;
            }
        }

        private HttpWebRequest CreateConnectivityRequest(string address, bool local = false)
        {
            if (address == null)
                return null;

            var host = new Uri(address);
            var uri = new Uri(host, "/identity");

            var request = (HttpWebRequest)WebRequest.Create(uri.ToString());
            request.Timeout = (local ? 1 : 5) * 1000;

            return request;
        }

        public override string ToString()
        {
            return Url;
        }
    }
}
