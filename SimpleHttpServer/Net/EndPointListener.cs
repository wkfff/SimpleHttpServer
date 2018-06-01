using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace SimpleHttpServer.Net
{
    sealed class EndPointListener
	{
		private IPEndPoint endpoint;
        private Socket sock;
        private Hashtable prefixes;
        private ArrayList unhandled;
        private ArrayList all;
        private X509Certificate2 cert;
        private AsymmetricAlgorithm key;
        private bool secure;
        private Dictionary<HttpConnection, HttpConnection> unregistered;

		public EndPointListener(IPAddress addr, int port, bool secure)
		{
			if (secure)
            {
				this.secure = secure;
				LoadCertificateAndKey(addr, port);
			}

			endpoint = new IPEndPoint(addr, port);
			sock = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
			sock.Bind(endpoint);
			sock.Listen(500);
			SocketAsyncEventArgs args = new SocketAsyncEventArgs();
			args.UserToken = this;
			args.Completed += OnAccept;
			sock.AcceptAsync(args);
			prefixes = new Hashtable();
			unregistered = new Dictionary<HttpConnection, HttpConnection>();
		}

		void LoadCertificateAndKey(IPAddress addr, int port)
		{
			try
            {
				string dirname = Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData);
				string path = Path.Combine (dirname, ".SimpleHttpServer");
				path = Path.Combine (path, "httplistener");
				string cert_file = Path.Combine (path, String.Format ("{0}.cer", port));
				if (!File.Exists (cert_file))
					return;

				string pvk_file = Path.Combine(path, String.Format ("{0}.pvk", port));
				if (!File.Exists (pvk_file))
					return;

				cert = new X509Certificate2 (cert_file);
				//key = PrivateKey.CreateFromFile(pvk_file).RSA;
			}
            catch
            {
				// ignore errors
			}
		}

		static void OnAccept(object sender, EventArgs e)
		{
			SocketAsyncEventArgs args = (SocketAsyncEventArgs)e;
			EndPointListener epl = (EndPointListener)args.UserToken;
			Socket accepted = null;
			if (args.SocketError == SocketError.Success)
            {
				accepted = args.AcceptSocket;
				args.AcceptSocket = null;
			}

			try
            {
				if (epl.sock != null)
					epl.sock.AcceptAsync(args);
			}
            catch
            {
				if (accepted != null)
                {
					try
                    {
						accepted.Close();
					}
                    catch
                    { }

					accepted = null;
				}
			} 

			if (accepted == null)
				return;

			if (epl.secure && (epl.cert == null || epl.key == null))
            {
				accepted.Close();
				return;
			}

			HttpConnection conn = new HttpConnection(accepted, epl, epl.secure, epl.cert, epl.key);
			lock (epl.unregistered)
            {
				epl.unregistered[conn] = conn;
			}

			conn.BeginReadRequest();
		}

		internal void RemoveConnection(HttpConnection conn)
		{
			lock (unregistered)
            {
				unregistered.Remove (conn);
			}
		}

		public bool BindContext(HttpListenerContext context)
		{
			HttpListenerRequest req = context.Request;
			ListenerPrefix prefix;
			HttpListener listener = SearchListener(req.Url, out prefix);
			if (listener == null)
				return false;

			context.Listener = listener;
			context.Connection.Prefix = prefix;
			return true;
		}

		public void UnbindContext(HttpListenerContext context)
		{
			if (context == null || context.Request == null)
				return;

			context.Listener.UnregisterContext(context);
		}

		HttpListener SearchListener(Uri uri, out ListenerPrefix prefix)
		{
			prefix = null;
			if (uri == null)
				return null;

			string host = uri.Host;
			int port = uri.Port;
			string path = HttpUtility.UrlDecode(uri.AbsolutePath);
			string path_slash = path[path.Length - 1] == '/' ? path : path + "/";
			
			HttpListener best_match = null;
			int best_length = -1;

			if (host != null && host != "")
            {
				Hashtable p_ro = prefixes;
				foreach (ListenerPrefix p in p_ro.Keys)
                {
					string ppath = p.Path;
					if (ppath.Length < best_length)
						continue;

					if (p.Host != host || p.Port != port)
						continue;

					if (path.StartsWith (ppath) || path_slash.StartsWith (ppath))
                    {
						best_length = ppath.Length;
						best_match = (HttpListener) p_ro [p];
						prefix = p;
					}
				}
				if (best_length != -1)
					return best_match;
			}

			ArrayList list = unhandled;
			best_match = MatchFromList(host, path, list, out prefix);
			if (path != path_slash && best_match == null)
				best_match = MatchFromList(host, path_slash, list, out prefix);
			if (best_match != null)
				return best_match;

			list = all;
			best_match = MatchFromList(host, path, list, out prefix);
			if (path != path_slash && best_match == null)
				best_match = MatchFromList(host, path_slash, list, out prefix);
			if (best_match != null)
				return best_match;

			return null;
		}

		HttpListener MatchFromList(string host, string path, ArrayList list, out ListenerPrefix prefix)
		{
			prefix = null;
			if (list == null)
				return null;

			HttpListener best_match = null;
			int best_length = -1;
			
			foreach (ListenerPrefix p in list)
            {
				string ppath = p.Path;
				if (ppath.Length < best_length)
					continue;

				if (path.StartsWith(ppath))
                {
					best_length = ppath.Length;
					best_match = p.Listener;
					prefix = p;
				}
			}

			return best_match;
		}

		void AddSpecial(ArrayList coll, ListenerPrefix prefix)
		{
			if (coll == null)
				return;

			foreach (ListenerPrefix p in coll)
            {
				if (p.Path == prefix.Path)
					throw new HttpListenerException(400, "Prefix already in use.");
			}

			coll.Add(prefix);
		}

		bool RemoveSpecial(ArrayList coll, ListenerPrefix prefix)
		{
			if (coll == null)
				return false;

			int c = coll.Count;
			for (int i = 0; i < c; i++)
            {
				ListenerPrefix p = (ListenerPrefix)coll[i];
				if (p.Path == prefix.Path)
                {
					coll.RemoveAt(i);
					return true;
				}
			}
			return false;
		}

		void CheckIfRemove()
		{
			if (prefixes.Count > 0)
				return;

			ArrayList list = unhandled;
			if (list != null && list.Count > 0)
				return;

			list = all;
			if (list != null && list.Count > 0)
				return;

			EndPointManager.RemoveEndPoint(this, endpoint);
		}

		public void Close()
		{
			sock.Close();
			lock (unregistered)
            {
				var connections = new List<HttpConnection>(unregistered.Keys);
				foreach (HttpConnection c in connections)
					c.Close(true);

				unregistered.Clear();
			}
		}

		public void AddPrefix(ListenerPrefix prefix, HttpListener listener)
		{
			ArrayList current;
			ArrayList future;
			if (prefix.Host == "*")
            {
				do
                {
					current = unhandled;
					future = (current != null) ? (ArrayList)current.Clone() : new ArrayList();
					prefix.Listener = listener;
					AddSpecial (future, prefix);
				} while (Interlocked.CompareExchange(ref unhandled, future, current) != current);
				return;
			}
			if (prefix.Host == "+")
            {
				do
                {
					current = all;
					future = (current != null) ? (ArrayList)current.Clone() : new ArrayList ();
					prefix.Listener = listener;
					AddSpecial (future, prefix);
				} while (Interlocked.CompareExchange(ref all, future, current) != current);
				return;
			}

			Hashtable prefs, p2;
			do
            {
				prefs = prefixes;
				if (prefs.ContainsKey(prefix))
                {
					HttpListener other = (HttpListener)prefs[prefix];
					if (other != listener)
						throw new HttpListenerException(400, "There's another listener for " + prefix);

					return;
				}

				p2 = (Hashtable)prefs.Clone();
				p2 [prefix] = listener;
			} while (Interlocked.CompareExchange (ref prefixes, p2, prefs) != prefs);
		}

		public void RemovePrefix(ListenerPrefix prefix, HttpListener listener)
		{
			ArrayList current;
			ArrayList future;
			if (prefix.Host == "*")
            {
				do
                {
					current = unhandled;
					future = (current != null) ? (ArrayList) current.Clone () : new ArrayList ();
					if (!RemoveSpecial (future, prefix))
						break; // Prefix not found
				} while (Interlocked.CompareExchange(ref unhandled, future, current) != current);

				CheckIfRemove();
				return;
			}

			if (prefix.Host == "+")
            {
				do
                {
					current = all;
					future = (current != null) ? (ArrayList)current.Clone() : new ArrayList();
					if (!RemoveSpecial (future, prefix))
						break; // Prefix not found
				} while (Interlocked.CompareExchange(ref all, future, current) != current);
				CheckIfRemove ();
				return;
			}

			Hashtable prefs, p2;
			do
            {
				prefs = prefixes;
				if (!prefs.ContainsKey (prefix))
					break;

				p2 = (Hashtable) prefs.Clone();
				p2.Remove (prefix);
			} while (Interlocked.CompareExchange(ref prefixes, p2, prefs) != prefs);
			CheckIfRemove ();
		}
	}
}