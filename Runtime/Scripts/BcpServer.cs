using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEditor.Experimental.GraphView;
using System.Threading.Tasks;


#if UNITY_EDITOR
using UnityEditor;
#endif


namespace BCP
{
    /// <summary>
    /// Singleton BCP Server class.  Manages the socket server to send and receive BCP messages.
    /// </summary>
    public class BcpServer : MonoBehaviour
    {
        /// <summary>
        /// The BCP Server version
        /// </summary>
        public const string CONTROLLER_VERSION = "0.53.0";

        /// <summary>
        /// The BCP Server name
        /// </summary>
        public const string CONTROLLER_NAME = "Unity Media Controller";

        /// <summary>
        /// The BCP Specification document version implemented
        /// </summary>
        public const string BCP_SPECIFICATION_VERSION = "1.1";

        [SerializeField]
        private int port;

        private CancellationTokenSource _receiveMessagesCancellationTokenSource = null;
        private TcpClient _client = null;

        /// <summary>
        /// Gets the static singleton object instance.
        /// </summary>
        /// <value>
        /// The instance.
        /// </value>
        public static BcpServer Instance { get; private set; }

        /// <summary>
        /// Gets a value indicating whether a client is currently connected.
        /// </summary>
        /// <value>
        ///   <c>true</c> if a client is currently connected; otherwise, <c>false</c>.
        /// </value>
        public bool ClientConnected
        {
            get { return (_client != null && _client.Connected); }
        }

        /// <summary>
        /// Called when the script instance is being loaded.
        /// </summary>
        void Awake()
        {
            // Save a reference to the BcpServer component as our singleton instance
            Instance = this;
            // Setup the socket communications between PC and MC (Unity) (start listening)
            BcpLogger.Trace($"Setting up BCP server (listening on port {port})");
            Init(port);
        }

        /// <summary>
        /// Called when the script instance is disabled.
        /// </summary>
        public void OnDestroy()
        {
            Close();
        }

        /// <summary>
        /// Initializes the BCP server and launches a reader worker thread to receive message from the MPF pin controller.
        /// </summary>
        /// <param name="port">The port to listen on.</param>
        public void Init(int port)
        {
            BcpLogger.Trace("BcpServer: Initializing");
            BcpLogger.Trace("BcpServer: " + CONTROLLER_NAME + " " + CONTROLLER_VERSION);
            BcpLogger.Trace("BcpServer: BCP Specification Version " + BCP_SPECIFICATION_VERSION);
            _receiveMessagesCancellationTokenSource = new CancellationTokenSource();
            Task.Run(() => ReceiveMessages(port, _receiveMessagesCancellationTokenSource.Token));
            BcpLogger.Trace("BcpServer: Waiting for a connection...");
        }

        /// <summary>
        /// OnGUI is called for rendering and handling GUI events.
        /// </summary>
        void OnGUI()
        {
#if UNITY_EDITOR
            // Display button to shutdown the server application (only when running in the editor)
            if (EditorApplication.isPlaying)
            {
                if (GUI.Button(new Rect(10, 10, 150, 50), "Shutdown Server"))
                {
                    Close();
                    EditorApplication.isPlaying = false;
                }
            }
#endif

            // Display waiting for connection message if client is not currently connected
            if (!ClientConnected)
            {
                // Create popup window rectangle in the center of the screen
                int screenWidth = Screen.width;
                int screenHeight = Screen.height;

                int windowWidth = 500;
                int windowHeight = 80;
                int windowX = (screenWidth - windowWidth) / 2;
                int windowY = 0;
                // int windowY = (screenHeight - windowHeight) / 2;

                // Position the window in the center of the screen.
                Rect windowRect0 = new Rect(windowX, windowY, windowWidth, windowHeight);

                // Display waiting for connection popup window
                GUILayout.Window(
                    0,
                    windowRect0,
                    WaitForConnectionMessage,
                    "Mission Pinball Framework"
                );
            }
        }

        /// <summary>
        /// Window function to display waiting for connection message.
        /// </summary>
        /// <param name="windowId">The window identifier.</param>
        void WaitForConnectionMessage(int windowId)
        {
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Client disconnected");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Waiting for connection from client...");
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private async Task ReceiveMessages(int port, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (TryEstablishConnection(ct, port, out var listener, out var client))
                {
                    _client = client;
                    using (client)
                    {
                        await ReceiveMessagesUntilDisconnect(client, ct);
                        client.Close();
                    }
                    _client = null;
                }
                listener.Stop();
            }
            BcpLogger.Trace("BcpServer: Receive task end");
        }

        /// <summary>
        /// Listens for a connection, interprets and enqueues messages from MPF pin controller client.
        /// </summary>
        private bool TryEstablishConnection(CancellationToken ct, int port, out TcpListener listener, out TcpClient client)
        {
            BcpLogger.Trace("BcpServer: ListenForConnection start");

            // Create TCP/IP socket
            listener = new TcpListener(IPAddress.Any, port);
            BcpLogger.Trace(
                "BcpServer: Establishing local endpoint for the socket ("
                    + listener.LocalEndpoint.ToString()
                    + ")"
            );

            listener.Start();
            BcpLogger.Trace("BcpServer: Waiting for client to connect...");

            client = null;

            while (!ct.IsCancellationRequested && client == null)
            {
                if (listener.Pending())
                    client = listener.AcceptTcpClient();
            }

            BcpLogger.Trace("BcpServer: ListenForConnection thread finish");
            bool connectionEstablished = client != null;
            return connectionEstablished;
        }

        /// <summary>
        /// Handles client communications with the MPF pin controller via TCP socket.
        /// </summary>
        /// <param name="client">The client.</param>
        /// <param name="ct">The cancellation token</param>
        /// <remarks>
        /// This function runs in the same thread as the  and receives all messages from the MPF pin controller client. These messages are
        /// posted to a message queue in a thread-safe manner for the main Unity thread to process them.
        /// </remarks>
        private async Task ReceiveMessagesUntilDisconnect(TcpClient client, CancellationToken ct)
        {
            if (client == null || ct.IsCancellationRequested)
                return;

            BcpLogger.Trace("BcpServer: HandleClientCommunications thread start");
            NetworkStream clientStream = client.GetStream();

            const int messageBufferSize = 1024;
            StringBuilder messageBuffer = new StringBuilder(messageBufferSize);
            byte[] buffer = new byte[messageBufferSize];

            while (!ct.IsCancellationRequested && client.Connected && clientStream.CanRead)
            {
                try
                {
                    var bytesRead = await clientStream.ReadAsync(buffer, 0, messageBufferSize, ct);                    

                    if (bytesRead > 0)
                    {
                        messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                        // Determine if message is complete (check for message termination character)
                        // If not complete, save the buffer contents and continue to read packets, appending
                        // to saved buffer.  Once completed, convert to a BCP message.
                        int terminationCharacterPos = 0;
                        while (
                            (terminationCharacterPos = messageBuffer.ToString().IndexOf("\n")) > -1
                        )
                        {
                            BcpLogger.Trace(
                                "BcpServer: >>>>>>>>>>>>>> Received raw message: "
                                    + messageBuffer.ToString(0, terminationCharacterPos + 1)
                            );

                            // Convert received data to a BcpMessage
                            BcpMessage message = BcpMessage.CreateFromRawMessage(
                                messageBuffer.ToString(0, terminationCharacterPos + 1)
                            );

                            if (message != null)
                            {
                                BcpLogger.Trace(
                                    "BcpServer: >>>>>>>>>>>>>> Received \""
                                        + message.Command
                                        + "\" message: "
                                        + message.ToString()
                                );

                                // Add BCP message to the queue to be processed
                                BcpMessageManager.Instance.AddMessageToQueue(message);
                            }

                            // Remove the converted message from the buffer
                            messageBuffer.Remove(0, terminationCharacterPos + 1);
                        }
                    }
                }
                catch (Exception e)
                {
                    // A socket error has occurred
                    BcpLogger.Trace("BcpServer: Client reader thread exception: " + e.ToString());
                }
            }

            BcpLogger.Trace("BcpServer: HandleClientCommunications thread finish");
            BcpLogger.Trace("BcpServer: Closing TCP/Socket client");
        }

        /// <summary>
        /// Closes the BCP Server.
        /// </summary>
        public void Close()
        {
            BcpLogger.Trace("BcpServer: Close start");
            try
            {
                if (ClientConnected)
                {
                    // Send goodbye message to connected client
                    Send(BcpMessage.GoodbyeMessage());
                }
            }
            catch { }
            _receiveMessagesCancellationTokenSource?.Cancel();
            _receiveMessagesCancellationTokenSource.Dispose();
            _receiveMessagesCancellationTokenSource = null;

            BcpLogger.Trace("BcpServer: Close finished");
        }

        /// <summary>
        /// Sends the specified BCP message to the MPF pin controller.
        /// </summary>
        /// <param name="message">The BCP message.</param>
        /// <remarks>
        /// This function is called by the main Unity thread and does not run in its own thread.  It will block the rest of the application
        /// while sending (should be a quick process unless MPF client has a communication failure).
        /// </remarks>
        public bool Send(BcpMessage message)
        {
            if (!ClientConnected)
                return false;

            try
            {
                NetworkStream clientStream = _client.GetStream();
                byte[] packet;
                int length = message.ToPacket(out packet);
                if (length > 0)
                {
                    clientStream.Write(packet, 0, length);
                    clientStream.Flush();
                    BcpLogger.Trace(
                        "BcpServer: <<<<<<<<<<<<<< Sending \""
                            + message.Command
                            + "\" message: "
                            + message.ToString()
                    );
                }
            }
            catch (Exception e)
            {
                BcpLogger.Trace(
                    "BcpServer: Sending \"" + message.Command + "\" message FAILED: " + e.ToString()
                );
                return false;
            }

            return true;
        }

        /// <summary>
        /// Sends a DMD frame to the MPF pin controller to be output to hardware.
        /// </summary>
        /// <param name="frameData">The DMD frame data (must be 4096 bytes).</param>
        /// <returns></returns>
        /// <remarks>
        /// Used by the media controller to send a DMD frame to the pin controller which the pin controller displays on the physical DMD.
        /// Note that this command does not used named parameters, rather, the data is sent after the command, like this:
        /// dmd_frame?<raw byte string>
        /// This command is a special one in that it’s sent with ASCII encoding instead of UTF-8.
        /// The data is a raw byte string that is exactly 4096 bytes. (1 bytes per pixel, 128×32 DMD resolution = 4096 pixels.) The 4 low bits
        /// of each byte are the intensity (0-15), and the 4 high bits are ignored.
        /// </remarks>
        public bool SendDmdFrame(byte[] frameData)
        {
            if (!ClientConnected)
                return false;

            try
            {
                if (frameData.Length != 4096)
                    throw new ArgumentException(
                        "Frame data is not the correct length ("
                            + frameData.Length
                            + " bytes, expected 4096 bytes)",
                        "frameData"
                    );

                NetworkStream clientStream = _client.GetStream();

                byte[] messageCommand = Encoding.ASCII.GetBytes("dmd_frame?");
                byte[] messageTermination = Encoding.ASCII.GetBytes("\n");

                clientStream.Write(messageCommand, 0, messageCommand.Length);
                clientStream.Write(frameData, 0, frameData.Length);
                clientStream.Write(messageTermination, 0, messageTermination.Length);
                clientStream.Flush();

                BcpLogger.Trace(
                    "BcpServer: <<<<<<<<<<<<<< Sending \"dmd_frame\" message: frame data ("
                        + frameData.Length
                        + " bytes)"
                );
            }
            catch (Exception e)
            {
                BcpLogger.Trace("BcpServer: Sending \"dmd_frame\" message FAILED: " + e.ToString());
                return false;
            }

            return true;
        }
    }
}
