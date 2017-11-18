﻿using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Boerman.TcpLib.Shared;

namespace Boerman.TcpLib.Client
{
    public partial class TcpClient
    {
        private StateObject _state;
        
        private readonly ClientSettings _clientSettings;

        private readonly ManualResetEvent _isConnected = new ManualResetEvent(false);
        private readonly ManualResetEvent _isSending = new ManualResetEvent(false);
        private bool _isShuttingDown;

        public event EventHandler<DataReceivedEventArgs> DataReceived;
        public event EventHandler<ConnectedEventArgs> Connected;
        public event EventHandler<DisconnectedEventArgs> Disconnected;

        public TcpClient(IPEndPoint endpoint)
        {
            _clientSettings = new ClientSettings
            {
                EndPoint = endpoint,
                ReconnectOnDisconnect = false,
                Encoding = Encoding.GetEncoding("utf-8")
            };
        }

        public TcpClient(ClientSettings settings)
        {
            _clientSettings = settings;
        }

        private void ExecuteFunction(Action<IAsyncResult> action, IAsyncResult param)
        {
            try
            {
                try
                {
                    action(param);
                }
                catch (ObjectDisposedException)
                {
                    // I guess theh object should've been disposed. Try it.
                    // Tcp client is already closed! Start it again.
                    Open();
                }
                catch (SocketException ex)
                {
                    StateObject state = param.AsyncState as StateObject;

                    switch (ex.NativeErrorCode)
                    {
                        case 10054: // An existing connection was forcibly closed by the remote host
                            if (_clientSettings.ReconnectOnDisconnect)
                            {
                                Close();
                                Open();
                            }
                            break;
                        case 10061:
                            // No connection could be made because the target machine actively refused it. Do nuthin'
                            // Usually the tool will try to reconnect every 10 seconds or so.
                            break;
                        default:

                            break;
                    }
                }
                catch (Exception)
                {
                    // Tcp client isn't connected (We'd better clean up the resources.)
                    StateObject state = param.AsyncState as StateObject;

                    state?.Socket.Dispose();

                    Open();
                }
            }
            catch (Exception)
            {
                Environment.Exit(1);    // Aaand hopefully the service will be restarted.
            }
        }

        /// <summary>
        /// Open the connection to a remote endpoint
        /// </summary>
        public void Open()
        {
            try
            {
                // Reset the variables which are set earlier
                _isConnected.Reset();
                _isSending.Reset();

                // Continue trying until there's a connection.
                bool success;
                
                do
                {
                    _state = new StateObject(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
                    _state.Socket.BeginConnect(_clientSettings.EndPoint, ConnectCallback, _state);

                    success = _isConnected.WaitOne(1000);
                } while (!success);

                // We are connected!

                _isShuttingDown = false;

                Common.InvokeEvent(this, Connected, new ConnectedEventArgs(_clientSettings.EndPoint));
                
                _state.Socket.BeginReceive(_state.ReceiveBuffer, 0, _state.ReceiveBufferSize, 0, ReceiveCallback,
                    _state);
            }
            catch (SocketException ex)
            {
                switch (ex.NativeErrorCode)
                {
                    case 10054: // An existing connection was forcibly closed by the remote host
                        if (_clientSettings.ReconnectOnDisconnect)
                        {
                            Close();
                            Open();
                        }
                        break;
                    default:
                        throw;
                }
            }
        }

        /// <summary>
        /// Close the connection to a remote endpoint
        /// </summary>
        public void Close()
        {
            try
            {
                _isShuttingDown = true;

                if (_state.Socket.IsConnected())
                {
                    // There's no specific reason to set a timeout as this operation
                    // should be completed pretty fast anyway.
                    _isSending.WaitOne();
                    _isSending.Reset();

                    _isConnected.Reset();

                    _state.Socket.Shutdown(SocketShutdown.Both);
                    _state.Socket.Disconnect(false);
                }

                _state.Socket.Dispose();
                Common.InvokeEvent(this, Disconnected, new DisconnectedEventArgs(_clientSettings.EndPoint));
            }
            catch (SocketException ex)
            {
                if (ex.ErrorCode == 10038)
                {
                    // Something is done on not a socket... 
                }
                else if (ex.ErrorCode == 10004)
                {
                    // Some blocking call was interrupted.
                }
                else
                {
                    throw;
                }
            }
            catch (ObjectDisposedException)
            {
                // We can just ignore this one :)
            }
        }

        /// <summary>
        /// Send the specified message.
        /// </summary>
        /// <param name="message">Message</param>
        public void Send(string message)
        {
            Send(_clientSettings.Encoding.GetBytes(message));
        }

        /// <summary>
        /// Send the specified data.
        /// </summary>
        /// <param name="data">Data</param>
        public void Send(byte[] data)
        {
            // Wait with the send process until we're connected.
            // ToDo: Check whether we have to add some timeout in case no connection can be made
            _isConnected.WaitOne();

            _state.OutboundMessages.Enqueue(data);
            
            if (_isSending.WaitOne(0)) {
                while (_state.OutboundMessages.Any())
                {
                    if (_isShuttingDown)
                    {
                        // Empty queue and return
                        while (_state.OutboundMessages.Any())
                        {
                            byte[] removedData;
                            _state.OutboundMessages.TryDequeue(out removedData);
                        }

                        return;
                    }

                    _state.OutboundMessages.TryDequeue(out _state.SendBuffer);

                    try
                    {
                        // We can only send one message at a time.
                        _state.Socket.BeginSend(_state.SendBuffer, 0, _state.SendBuffer.Length, 0, SendCallback,
                            _state);
                    }
                    catch (SocketException ex)
                    {
                        switch (ex.NativeErrorCode)
                        {
                            case 32:    // Broken pipe
                            case 10054: // An existing connection was forcibly closed by the remote host
                                _isSending.Set(); // Otherwise the program will wait indefinitely.

                                if (_clientSettings.ReconnectOnDisconnect)
                                {
                                    Close();
                                    Open();
                                }
                                break;
                            default:
                                throw;
                        }
                    }

                    // Wait until we're cleared to send another message
                    _isSending.WaitOne();
                }
            }
        }

        private void ConnectCallback(IAsyncResult ar)
        {
            ExecuteFunction(delegate (IAsyncResult result)
            {
                StateObject state = (StateObject)result.AsyncState;
                state.Socket.EndConnect(result);

                _isConnected.Set();
                _isSending.Set();
            }, ar);
        }

        private void SendCallback(IAsyncResult ar)
        {
            StateObject state = null;
            int bytesSend = 0;

            ExecuteFunction(delegate (IAsyncResult result)
            {
                state = (StateObject)result.AsyncState;
                bytesSend = state.Socket.EndSend(result);
            }, ar);

            // If code down here is put in the `ExecuteFunction` wrapper and shit hits the fan
            // `_isSending` reset event would never be set thus never allowing the socket to close.
            if (state == null)
            {
                // Just make sure the program can continue running.
                _isSending.Set();
                return;
            }

            state.SendBuffer = state.SendBuffer.Skip(bytesSend).ToArray();

            _isSending.Set();
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            ExecuteFunction(delegate (IAsyncResult result)
            {
                StateObject state = (StateObject)result.AsyncState;
                state.LastConnection = DateTime.UtcNow;

                Socket handler = state.Socket;

                if (handler.IsConnected())
                {
                    handler.BeginReceive(state.ReceiveBuffer, 0, state.ReceiveBufferSize, 0, ReceiveCallback, state);
                }
                else
                {
                    Close();

                    // Don't ask what I need this for
                    if (_clientSettings.ReconnectOnDisconnect)
                    {
                        Close();
                        Open();
                    }

                    return;
                }

                int bytesRead = handler.EndReceive(result);

                var str = _clientSettings.Encoding.GetString(state.ReceiveBuffer, 0, bytesRead);

                Common.InvokeEvent(this, DataReceived, new DataReceivedEventArgs(str, state.Endpoint));
            }, ar);
        }
    }
}
