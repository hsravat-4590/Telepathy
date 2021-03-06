﻿﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Telepathy
{
    public class Server : Common
    {
        // events to hook into
        // => OnData uses ArraySegment for allocation free receives later
        public Action<int> OnConnected;
        public Action<int, ArraySegment<byte>> OnData;
        public Action<int> OnDisconnected;

        // listener
        public TcpListener listener;
        Thread listenerThread;

        // clients with <connectionId, ConnectionState>
        readonly ConcurrentDictionary<int, ConnectionState> clients = new ConcurrentDictionary<int, ConnectionState>();
        volatile bool _Encrypted;
        public bool Encrypted => _Encrypted;

        volatile string _CertFile;
        public string CertFile => _CertFile;

        // class with all the client's data. let's call it Token for consistency
        // with the async socket methods.
        class ClientToken
        {
            public TcpClient client;

        // connectionId counter
        int counter;

        // public next id function in case someone needs to reserve an id
        // (e.g. if hostMode should always have 0 connection and external
        //  connections should start at 1, etc.)
        public int NextConnectionId()
        {
            int id = Interlocked.Increment(ref counter);

            // it's very unlikely that we reach the uint limit of 2 billion.
            // even with 1 new connection per second, this would take 68 years.
            // -> but if it happens, then we should throw an exception because
            //    the caller probably should stop accepting clients.
            // -> it's hardly worth using 'bool Next(out id)' for that case
            //    because it's just so unlikely.
            if (id == int.MaxValue)
            {
                throw new Exception("connection id limit reached: " + id);
            }

            return id;
        }

        // check if the server is running
        public bool Active => listenerThread != null && listenerThread.IsAlive;

        // constructor
        public Server(int MaxMessageSize) : base(MaxMessageSize) {}

        // the listener thread's listen function
        // note: no maxConnections parameter. high level API should handle that.
        //       (Transport can't send a 'too full' message anyway)
        void Listen(int port)
        {
            // absolutely must wrap with try/catch, otherwise thread
            // exceptions are silent
            try
            {
                // start listener on all IPv4 and IPv6 address via .Create
                listener = TcpListener.Create(port);
                listener.Server.NoDelay = NoDelay;
                listener.Server.SendTimeout = SendTimeout;
                listener.Start();
                Log.Info("Server: listening port=" + port);

                X509Certificate cert = null;
                bool selfSignedCert = false;
                
                if (Encrypted)
                {
                    if (CertFile == null)
                    {
                        // Create a new self-signed certificate
                        selfSignedCert = true;
                        cert =
                            new X509Certificate2Builder
                            {
                                SubjectName = string.Format("CN={0}", ((IPEndPoint)listener.LocalEndpoint).Address.ToString())
                            }.Build();
                    }
                    else
                    {
                        cert = X509Certificate.CreateFromCertFile(CertFile);
                        selfSignedCert = false;
                    }
                }

                // keep accepting new clients
                while (true)
                {
                    // wait and accept new client
                    // note: 'using' sucks here because it will try to
                    // dispose after thread was started but we still need it
                    // in the thread
                    TcpClient client = listener.AcceptTcpClient();

                    // set socket options
                    client.NoDelay = NoDelay;
                    client.SendTimeout = SendTimeout;

                    // generate the next connection id (thread safely)
                    int connectionId = NextConnectionId();

                    // add to dict immediately
                    ConnectionState connection = new ConnectionState(client, MaxMessageSize);
                    clients[connectionId] = connection;

                    Stream stream = client.GetStream();
                    
                    Thread sslAuthenticator = null;
                    if (Encrypted)
                    {
                        RemoteCertificateValidationCallback trustCert = (object sender, X509Certificate x509Certificate,
                            X509Chain x509Chain, SslPolicyErrors policyErrors) =>
                        {
                            if (selfSignedCert)
                            {
                                // All certificates are accepted
                                return true;
                            }
                            else
                            {
                                if (policyErrors == SslPolicyErrors.None)
                                {
                                    return true;
                                }
                                else
                                {
                                    return false;
                                }
                            }
                        };

						SslStream sslStream = new SslStream(client.GetStream(), false, trustCert);
                        stream = sslStream;

                        sslAuthenticator = new Thread(() => {
                            try
                            {
                                // Using System.Security.Authentication.SslProtocols.None (the Microsoft recommended parameter which
                                // chooses the highest version of TLS) does not seem to work with Unity. Unity 2018.2 added support
                                // for TLS 1.2 when used with the .NET 4.x runtime, so use preprocessor directives to choose the right protocol
#if UNITY_2018_2_OR_NEWER && NET_4_6
                                System.Security.Authentication.SslProtocols protocol = System.Security.Authentication.SslProtocols.Tls12;
#else
                                System.Security.Authentication.SslProtocols protocol = System.Security.Authentication.SslProtocols.Default;
#endif

                                bool checkCertificateRevocation = !selfSignedCert;
                                sslStream.AuthenticateAsServer(cert, false, protocol, checkCertificateRevocation);
                            }
                            catch (Exception exception)
                            {
                                Logger.LogError("SSL Authenticator exception: " + exception);
                            }
                        });

                        sslAuthenticator.IsBackground = true;
                        sslAuthenticator.Start();
                    }

                    // spawn a send thread for each client
                    Thread sendThread = new Thread(() =>
                    {
                        // wrap in try-catch, otherwise Thread exceptions
                        // are silent
                        try
                        {
                            if (sslAuthenticator != null)
                            {
                                sslAuthenticator.Join();
                            }

                            // run the send loop
                            // IMPORTANT: DO NOT SHARE STATE ACROSS MULTIPLE THREADS!
                            ThreadFunctions.SendLoop(connectionId, client, connection.sendPipe, connection.sendPending);
                        }
                        catch (ThreadAbortException)
                        {
                            // happens on stop. don't log anything.
                            // (we catch it in SendLoop too, but it still gets
                            //  through to here when aborting. don't show an
                            //  error.)
                        }
                        catch (Exception exception)
                        {
                            Log.Error("Server send thread exception: " + exception);
                        }
                    });
                    sendThread.IsBackground = true;
                    sendThread.Start();

                    // spawn a receive thread for each client
                    Thread receiveThread = new Thread(() =>
                    {
                        // wrap in try-catch, otherwise Thread exceptions
                        // are silent
                        try
                        {
                            if (sslAuthenticator != null)
                            {
                                sslAuthenticator.Join();
                            }

                            // run the receive loop
                            // IMPORTANT: DO NOT SHARE STATE ACROSS MULTIPLE THREADS!
                            ThreadFunctions.ReceiveLoop(connectionId, client, MaxMessageSize, connection.receivePipe, QueueLimit);

                            // IMPORTANT: do NOT remove from clients after the
                            // thread ends. need to do it in Tick() so that the
                            // disconnect event in the pipe is still processed.
                            // (removing client immediately would mean that the
                            //  pipe is lost and the disconnect event is never
                            //  processed)

                            // sendthread might be waiting on ManualResetEvent,
                            // so let's make sure to end it if the connection
                            // closed.
                            // otherwise the send thread would only end if it's
                            // actually sending data while the connection is
                            // closed.
                            sendThread.Interrupt();
                        }
                        catch (Exception exception)
                        {
                            Log.Error("Server client thread exception: " + exception);
                        }
                    });
                    receiveThread.IsBackground = true;
                    receiveThread.Start();
                }
            }
            catch (ThreadAbortException exception)
            {
                // UnityEditor causes AbortException if thread is still
                // running when we press Play again next time. that's okay.
                Log.Info("Server thread aborted. That's okay. " + exception);
            }
            catch (SocketException exception)
            {
                // calling StopServer will interrupt this thread with a
                // 'SocketException: interrupted'. that's okay.
                Log.Info("Server Thread stopped. That's okay. " + exception);
            }
            catch (Exception exception)
            {
                // something went wrong. probably important.
                Log.Error("Server Exception: " + exception);
            }
        }

        // start listening for new connections in a background thread and spawn
        // a new thread for each one.
        public bool Start(int port, bool encrypt, string certFile)
        {
            // not if already started
            if (Active) return false;

            _Encrypted = encrypt;
            _CertFile = certFile;

            // clear old messages in queue, just to be sure that the caller
            // doesn't receive data from last time and gets out of sync.
            // -> calling this in Stop isn't smart because the caller may
            //    still want to process all the latest messages afterwards
            receiveQueue = new ConcurrentQueue<Message>();

            // start the listener thread
            // (on low priority. if main thread is too busy then there is not
            //  much value in accepting even more clients)
            Log.Info("Server: Start port=" + port);
            listenerThread = new Thread(() => { Listen(port); });
            listenerThread.IsBackground = true;
            listenerThread.Priority = ThreadPriority.BelowNormal;
            listenerThread.Start();
            return true;
        }

        public bool Start(int port)
        {
            return Start(port, false, null);
        }

        public void Stop()
        {
            // only if started
            if (!Active) return;

            Log.Info("Server: stopping...");

            // stop listening to connections so that no one can connect while we
            // close the client connections
            // (might be null if we call Stop so quickly after Start that the
            //  thread was interrupted before even creating the listener)
            listener?.Stop();

            // kill listener thread at all costs. only way to guarantee that
            // .Active is immediately false after Stop.
            // -> calling .Join would sometimes wait forever
            listenerThread?.Interrupt();
            listenerThread = null;

            // close all client connections
            foreach (KeyValuePair<int, ConnectionState> kvp in clients)
            {
                TcpClient client = kvp.Value.client;
                // close the stream if not closed yet. it may have been closed
                // by a disconnect already, so use try/catch
                try { client.GetStream().Close(); } catch {}
                client.Close();
            }

            // clear clients list
            clients.Clear();

            // reset the counter in case we start up again so
            // clients get connection ID's starting from 1
            counter = 0;
        }

        // send message to client using socket connection.
        // arraysegment for allocation free sends later.
        // -> the segment's array is only used until Send() returns!
        public bool Send(int connectionId, ArraySegment<byte> message)
        {
            // respect max message size to avoid allocation attacks.
            if (message.Count <= MaxMessageSize)
            {
                // find the connection
                if (clients.TryGetValue(connectionId, out ConnectionState connection))
                {
                    // check send pipe limit
                    if (connection.sendPipe.Count < QueueLimit)
                    {
                        // add to thread safe send pipe and return immediately.
                        // calling Send here would be blocking (sometimes for long
                        // times if other side lags or wire was disconnected)
                        connection.sendPipe.Enqueue(message);
                        connection.sendPending.Set(); // interrupt SendThread WaitOne()
                        return true;
                    }
                    // disconnect if send queue gets too big.
                    // -> avoids ever growing queue memory if network is slower
                    //    than input
                    // -> disconnecting is great for load balancing. better to
                    //    disconnect one connection than risking every
                    //    connection / the whole server
                    //
                    // note: while SendThread always grabs the WHOLE send queue
                    //       immediately, it's still possible that the sending
                    //       blocks for so long that the send queue just gets
                    //       way too big. have a limit - better safe than sorry.
                    else
                    {
                        // log the reason
                        Log.Warning($"Server.Send: sendPipe for connection {connectionId} reached limit of {QueueLimit}. This can happen if we call send faster than the network can process messages. Disconnecting this connection for load balancing.");

                        // just close it. send thread will take care of the rest.
                        connection.client.Close();
                        return false;
                    }
                }

                // sending to an invalid connectionId is expected sometimes.
                // for example, if a client disconnects, the server might still
                // try to send for one frame before it calls GetNextMessages
                // again and realizes that a disconnect happened.
                // so let's not spam the console with log messages.
                //Logger.Log("Server.Send: invalid connectionId: " + connectionId);
                return false;
            }
            Log.Error("Server.Send: message too big: " + message.Count + ". Limit: " + MaxMessageSize);
            return false;
        }

        // client's ip is sometimes needed by the server, e.g. for bans
        public string GetClientAddress(int connectionId)
        {
            // find the connection
            if (clients.TryGetValue(connectionId, out ConnectionState connection))
            {
                return ((IPEndPoint)connection.client.Client.RemoteEndPoint).Address.ToString();
            }
            return "";
        }

        // disconnect (kick) a client
        public bool Disconnect(int connectionId)
        {
            // find the connection
            if (clients.TryGetValue(connectionId, out ConnectionState connection))
            {
                // just close it. send thread will take care of the rest.
                connection.client.Close();
                Log.Info("Server.Disconnect connectionId:" + connectionId);
                return true;
            }
            return false;
        }

        // tick: processes up to 'limit' messages for each connection
        // => limit parameter to avoid deadlocks / too long freezes if server or
        //    client is too slow to process network load
        // => Mirror & DOTSNET need to have a process limit anyway.
        //    might as well do it here and make life easier.
        // => returns amount of remaining messages to process, so the caller
        //    can call tick again as many times as needed (or up to a limit)
        //
        // ticking EVERY CONNECTION up to 'limit is way better for stability.
        // previously we had one receive pipe for all, so if one connection
        // would spam the pipe, everyone else would be delayed.
        //
        // IMPORTANT: Mirror & DOTSNET call tick multiple times up to limit.
        //            doing the limit IN HERE IS FASTER because we don't need to
        //            iterate all connections each limit. instead we iterate
        //            ONLY ONCE and process 'limit' messages for each connection
        //            => THIS IS EXTREMELY IMPORTANT FOR PERFORMANCE!
        //
        // Tick() may process multiple messages, but Mirror needs a way to stop
        // processing immediately if a scene change messages arrives. Mirror
        // can't process any other messages during a scene change.
        // (could be useful for others too)
        // => make sure to allocate the lambda only once in transports
        List<int> connectionsToRemove = new List<int>();
        public int Tick(int processLimit, Func<bool> checkEnabled = null)
        {
            int remaining = 0;

            // for each connection
            // checks enabled in case a Mirror scene message arrived
            foreach (KeyValuePair<int, ConnectionState> kvp in clients)
            {
                MagnificentReceivePipe receivePipe = kvp.Value.receivePipe;

                // need a processLimit copy just for this connection so that
                // we can count the Connected message as a processed one.
                // => otherwise decreasing the limit in Connected event would
                //    decrease the limit for everyone!
                int connectionProcessLimit = processLimit;

                // always process connect FIRST before anything else
                if (connectionProcessLimit > 0)
                {
                    if (receivePipe.CheckConnected())
                    {
                        OnConnected?.Invoke(kvp.Key);
                        // it counts as a processed message
                        --connectionProcessLimit;
                    }
                }

                // process up to 'processLimit' messages for this connection
                // checks enabled in case a Mirror scene message arrived
                for (int i = 0; i < connectionProcessLimit; ++i)
                {
                    // check enabled in case a Mirror scene message arrived
                    if (checkEnabled != null && !checkEnabled())
                        break;

                    // peek first. allows us to process the first queued entry while
                    // still keeping the pooled byte[] alive by not removing anything.
                    if (receivePipe.TryPeek(out ArraySegment<byte> message))
                    {
                        OnData?.Invoke(kvp.Key, message);

                        // IMPORTANT: now dequeue and return it to pool AFTER we are
                        //            done processing the event.
                        receivePipe.TryDequeue();
                    }

                    // AFTER PROCESSING, add remaining ones to our counter
                    remaining += receivePipe.Count;
                }

                // always process disconnect AFTER anything else
                // (should never process data messages after disconnect message)
                if (connectionProcessLimit > 0)
                {
                    if (receivePipe.CheckDisconnected())
                    {
                        OnDisconnected?.Invoke(kvp.Key);
                        connectionsToRemove.Add(kvp.Key);
                    }
                }
            }

            // remove all disconnected connections now that we processed the
            // final disconnect message.
            foreach (int connectionId in connectionsToRemove)
                clients.TryRemove(connectionId, out ConnectionState _);
            connectionsToRemove.Clear();

            return remaining;
        }
    }
}
