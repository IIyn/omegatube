using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OmegaTube
{
    // The core's log, read over the network. The TV restricts sdb (`shell` and `pull` are refused), and the UDP mirror
    // needs the TV to reach this machine, which a locked-down network may not allow — but the laptop reaching the TV
    // always works, so the app serves its own log instead:
    //
    //     http://<tv ip>:8078/log.txt          this session and the previous one, as the bug report shows them
    //     http://<tv ip>:8078/ping             "ok", to check the port
    //
    // Read only, and no path is taken from the client, so the only thing it can be asked for is the log itself.
    public static class LogServer
    {
        public const int Port = 8078;

        private static TcpListener listener;
        private static Thread thread;
        private static volatile bool running;
        private static string dataDir;

        public static void Start(string dir)
        {
            if (running)
            {
                return;
            }
            dataDir = dir;
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
            }
            catch (Exception e)
            {
                Tizen.Log.Warn("OmegaTube", "log server: " + e.Message);
                return;   // the port is taken: the log is still on the TV, just not readable over the network
            }
            running = true;
            thread = new Thread(Loop) { IsBackground = true };
            thread.Start();
            Tizen.Log.Info("OmegaTube", "log server on port " + Port);
        }

        public static void Stop()
        {
            running = false;
            try
            {
                listener?.Stop();
            }
            catch (Exception)
            {
            }
            listener = null;
        }

        private static void Loop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;   // stopped
                }
                try
                {
                    Serve(client);
                }
                catch (Exception e)
                {
                    Tizen.Log.Warn("OmegaTube", "log server: " + e.Message);
                }
                finally
                {
                    try
                    {
                        client.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static void Serve(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            using (NetworkStream stream = client.GetStream())
            {
                string request = ReadRequestLine(stream);
                if (request == null)
                {
                    return;
                }
                string path = request.Split(' ').Length > 1 ? request.Split(' ')[1] : "/";
                string body;
                if (path.StartsWith("/log.txt"))
                {
                    body = "=== this session ===\n" + Read("omega.log") + "\n=== previous session ===\n" + Read("omega.log.prev");
                }
                else if (path.StartsWith("/ping"))
                {
                    body = "ok";
                }
                else
                {
                    body = "log.txt or ping\n";
                }
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                string head = "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n" +
                              "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
                byte[] headBytes = Encoding.ASCII.GetBytes(head);
                stream.Write(headBytes, 0, headBytes.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }

        private static string ReadRequestLine(NetworkStream stream)
        {
            var line = new StringBuilder();
            for (int i = 0; i < 2048; i++)
            {
                int b = stream.ReadByte();
                if (b < 0 || b == '\n')
                {
                    break;
                }
                if (b != '\r')
                {
                    line.Append((char)b);
                }
            }
            return line.Length > 0 ? line.ToString() : null;
        }

        private static string Read(string name)
        {
            try
            {
                string path = Path.Combine(dataDir, name);
                return File.Exists(path) ? File.ReadAllText(path) : "(none)";
            }
            catch (Exception e)
            {
                return "(cannot read " + name + ": " + e.Message + ")";
            }
        }
    }
}
