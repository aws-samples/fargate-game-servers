// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System.Net;
using System.Net.Sockets;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Amazon.Lambda;
using Amazon;
using UnityEngine.SceneManagement;
using System.IO;

#if SERVER

[Serializable]
public class TaskData
{
    public string TaskARN;
}

public class GameServerStatusData
{
    public string taskArn { get; set; } //arn of the task the server is running on (inluding container ID)
    public bool serverInUse { get; set; } //Is this server in use already (claimed and max amount of players connected)
    public int currentPlayers { get; set; } //amount of current players on the server
    public int maxPlayers { get; set; } //Max amount of players we accept
    public bool ready { get; set; } //Are we ready to accept clients?
    public string publicIP { get; set; } //IP of the instance 
    public int port { get; set; } //Port used
    public bool serverTerminated { get; set; } //Tells the backend that server has terminated and needs to be deleted from redis
    public int gameSessionsHosted { get; set; } //How many game sessions this server has already hosted? Used for terminating the Task when defined maximum is reached
}

public class TaskStatusData
{
    public string taskArn { get; set; } //arn of the task the server is running on (withut the container ID)
}

// *** MONOBEHAVIOUR TO MANAGE SERVER LOGIC *** //

public class Server : MonoBehaviour
{
    // TODO: Update this to your selected Region
    RegionEndpoint regionEndpoint = RegionEndpoint.USEast1;

    // How many times the game server is reused for sessions. If you expect very little memory leaks or other issues/crashes, it could be higher
    public static int totalGameSessionsToHost = 3;
    public static int hostedGameSessions = 0;

    // For testing we have maximum of 2 players
    public static int maxPlayers = 2;

    public static int port = -1;

    public static float redisUpdateIntervalSeconds = 15.0f;

    //We get events back from the NetworkServer through this static list
    public static List<SimpleMessage> messagesToProcess = new List<SimpleMessage>();

    // Game server data
    private string taskDataArn = null;
    private string taskDataArnWithContainer = null;
    private string publicIP = null;

    float redisUpdateCounter = 0.0f;

    private int lastPlayerCount = 0;

    NetworkServer server;

    // Game state
    private bool gameStarted = false;

    // Defines if we're waiting to be terminated (maximum amount of game sessions hosted)
    public bool waitingForTermination = false;
    float waitingForTerminateCounter = 5.0f; //We start by checking immediately so set to full 5 seconds

    public void StartGame()
    {
        System.Console.WriteLine("Starting game");
        this.gameStarted = true;
    }

    public bool GameStarted()
    {
        return this.gameStarted;
    }

    // Start is called before the first frame update
    void Start()
    {
        // Get the port for this container
        Server.port = int.Parse(Environment.GetEnvironmentVariable("PORT"));
        Console.WriteLine("My port: " + Server.port);

        server = new NetworkServer(this);

        // Get my IP information through ipify
        var requestIPAddressPath = "https://api.ipify.org";
        StartCoroutine(GetMyIP(requestIPAddressPath));

        // Get my Task information for Redis
        var fargateMetadataPath = Environment.GetEnvironmentVariable("ECS_CONTAINER_METADATA_URI");
        Console.WriteLine("Fargate metadata path: " + fargateMetadataPath);
        // Request the metadata
        StartCoroutine(GetTaskMetadata(fargateMetadataPath + "/task"));

        // Set Redis update counter to trigger in a few seconds to inform that we're ready as soon as possible
        this.redisUpdateCounter = Server.redisUpdateIntervalSeconds - 2.0f;
    }

    IEnumerator GetMyIP(string uri)
    {
        using (UnityWebRequest webRequest = UnityWebRequest.Get(uri))
        {
            // Request and wait for the desired page.
            yield return webRequest.SendWebRequest();

            if (webRequest.isNetworkError)
            {
                Debug.Log("Web request Error: " + webRequest.error);
            }
            else
            {
                Debug.Log("Web Request Received: " + webRequest.downloadHandler.text);
                // Set the public IP
                this.publicIP = webRequest.downloadHandler.text;
            }
        }
    }

    IEnumerator GetTaskMetadata(string uri)
    {
        using (UnityWebRequest webRequest = UnityWebRequest.Get(uri))
        {
            // Request and wait for the desired page.
            yield return webRequest.SendWebRequest();

            if (webRequest.isNetworkError)
            {
                Debug.Log("Web request Error: " + webRequest.error);
            }
            else
            {
                Debug.Log("Web Request Received: " + webRequest.downloadHandler.text);
                var taskData = JsonUtility.FromJson<TaskData>(webRequest.downloadHandler.text);
                this.taskDataArn = taskData.TaskARN;
                this.taskDataArnWithContainer = taskData.TaskARN + "-" + Environment.GetEnvironmentVariable("CONTAINERNAME"); //Including the container name as we run multiple containers in a Task
                Debug.Log("TaskARN: " + this.taskDataArn);
                Debug.Log("TaskARN with container: " + this.taskDataArnWithContainer);
            }
        }
    }

    // Update is called once per frame
    void Update()
    {
        // 1. If we're waiting for termination, only check if all containers in this Task are done
        if (this.waitingForTermination)
        {
            this.waitingForTerminateCounter += Time.deltaTime;
            // Check the status every 5 seconds
            if (waitingForTerminateCounter > 5.0f)
            {
                this.waitingForTerminateCounter = 0.0f;

                Debug.Log("Waiting for other servers in the Task to finish...");

                var lambdaConfig = new AmazonLambdaConfig() { RegionEndpoint = this.regionEndpoint };
                var lambdaClient = new Amazon.Lambda.AmazonLambdaClient(lambdaConfig);

                // Call Lambda function to check if we should terminate
                var taskStatusRequestData = new TaskStatusData();
                taskStatusRequestData.taskArn = this.taskDataArn;
                var request = new Amazon.Lambda.Model.InvokeRequest()
                {
                    FunctionName = "FargateGameServersCheckIfAllContainersInTaskAreDone",
                    Payload = JsonConvert.SerializeObject(taskStatusRequestData),
                    InvocationType = InvocationType.RequestResponse
                };

                // As we are not doing anything else on the server anymore, we can just wait for the invoke response
                var invokeResponse = lambdaClient.InvokeAsync(request);
                invokeResponse.Wait();
                invokeResponse.Result.Payload.Position = 0;
                var sr = new StreamReader(invokeResponse.Result.Payload);
                var responseString = sr.ReadToEnd();

                Debug.Log("Got response: " + responseString);

                // Try catching to boolean, if it was a failure, this will also result in false
                var allServersInTaskDone = false;
                bool.TryParse(responseString, out allServersInTaskDone);

                if (allServersInTaskDone)
                {
                    Debug.Log("All servers in the Task done running full amount of sessions --> Terminate");
                    Application.Quit();
                }
            }
            return;
        }

        // 2. Otherwise run regular update

        server.Update();

        // Go through any messages to process (on the game world)
        foreach (SimpleMessage msg in messagesToProcess)
        {
            // NOTE: We should spawn players and set positions also on server side here and validate actions. For now we just pass this data to clients
        }
        messagesToProcess.Clear();

        // Update the server state to Redis every 30 seconds with a 60 second expiration. This will also get done when new clients connect
        this.redisUpdateCounter += Time.deltaTime;
        if(this.redisUpdateCounter > Server.redisUpdateIntervalSeconds && this.taskDataArnWithContainer != null)
        {
            this.UpdateRedis();
            this.redisUpdateCounter = 0.0f;
        }

        // If a new player joined, update Redis as well
        if(this.server.GetPlayerCount() > this.lastPlayerCount)
        {
            Debug.Log("New player joined, update Redis");
            this.UpdateRedis();
            this.lastPlayerCount = this.server.GetPlayerCount();
        }
    }

    public void DisconnectAll()
    {
        this.server.DisconnectAll();
    }

    public void UpdateRedis(bool serverTerminated = false)
    {
        var gameServerStatusData = new GameServerStatusData();
        gameServerStatusData.taskArn = this.taskDataArnWithContainer;
        gameServerStatusData.currentPlayers = this.server.GetPlayerCount();
        gameServerStatusData.maxPlayers = Server.maxPlayers;
        gameServerStatusData.publicIP = this.publicIP;
        gameServerStatusData.port = Server.port;
        gameServerStatusData.serverTerminated = serverTerminated;
        gameServerStatusData.gameSessionsHosted = Server.hostedGameSessions;

        var lambdaConfig = new AmazonLambdaConfig() { RegionEndpoint = this.regionEndpoint };
        lambdaConfig.MaxErrorRetry = 0; //Don't do retries on failures
        var lambdaClient = new Amazon.Lambda.AmazonLambdaClient(lambdaConfig);

        // Option 1. If TCPListener is not ready yet, update as not ready
        if (!this.server.IsReady())
        {
            Debug.Log("Updating as not ready yet to Redis");
            gameServerStatusData.ready = false;
            gameServerStatusData.serverInUse = false;
        }
        // Option 2. If not full yet but, update our status as ready
        else if (this.server.IsReady() && this.server.GetPlayerCount() < Server.maxPlayers)
        {
            Debug.Log("Updating as ready to Redis");
            gameServerStatusData.ready = true;
            gameServerStatusData.serverInUse = false;
        }
        // Option 3. If full, make sure the available key is deleted in Redis and update the full key
        else
        {
            Debug.Log("Updating as full to Redis");
            gameServerStatusData.ready = true;
            gameServerStatusData.serverInUse = true;
        }

        // Call Lambda function to update status
        var request = new Amazon.Lambda.Model.InvokeRequest()
        {
            FunctionName = "FargateGameServersUpdateGameServerData",
            Payload = JsonConvert.SerializeObject(gameServerStatusData),
            InvocationType = InvocationType.Event
        };

        // NOTE: We could catch response to validate it was successful and do something useful with that information
        lambdaClient.InvokeAsync(request);
    }

}

// *** SERVER NETWORK LOGIC *** //

public class NetworkServer
{
	private TcpListener listener;
    private List<TcpClient> clients = new List<TcpClient>();
    private List<TcpClient> readyClients = new List<TcpClient>();
    private List<TcpClient> clientsToRemove = new List<TcpClient>();

    private Server server = null;

    private bool ready = false;

    public int GetPlayerCount() { return clients.Count; }
    public bool IsReady() { return this.ready; }

    // Ends the game session for all and disconnects the players
    public void TerminateGameSession()
    {
        // Check if we should already quit or host another session
        Server.hostedGameSessions++;
        if(Server.hostedGameSessions >= Server.totalGameSessionsToHost)
        {
            Debug.Log("Hosted max sessions, mark server as terminated to Redis and start waiting for other servers in the Task to terminate");
            this.server.UpdateRedis(serverTerminated: true);
            this.server.waitingForTermination = true;
        }
        else
        {
            Debug.Log("Restart the Server");
            Server.messagesToProcess = new List<SimpleMessage>(); //Reset messages
            this.ready = false;
            this.server.UpdateRedis(serverTerminated: false); //Update to redis as not ready while waiting for restart
            SceneManager.LoadScene("GameWorld"); // Reset world to restart everything
        }
    }

    public NetworkServer(Server server)
	{
        this.server = server;

        //Start the TCP server
        int port = Server.port;
        Debug.Log("Starting server on port " + port);
        listener = new TcpListener(IPAddress.Any, Server.port);
        Debug.Log("Listening at: " + listener.LocalEndpoint.ToString());
		listener.Start();
        this.ready = true;
	}

    // Checks if socket is still connected
    private bool IsSocketConnected(TcpClient client)
    {
        var bClosed = false;

        // Detect if client disconnected
        if (client.Client.Poll(0, SelectMode.SelectRead))
        {
            byte[] buff = new byte[1];
            if (client.Client.Receive(buff, SocketFlags.Peek) == 0)
            {
                // Client disconnected
                bClosed = true;
            }
        }

        return !bClosed;
    }

    public void Update()
	{
		// Are there any new connections pending?
		if (listener.Pending())
		{
            System.Console.WriteLine("Client pending..");
			TcpClient client = listener.AcceptTcpClient();
            client.NoDelay = true; // Use No Delay to send small messages immediately. UDP should be used for even faster messaging
            System.Console.WriteLine("Client accepted.");

            // We have a maximum of 2 clients per game
            if(this.clients.Count < Server.maxPlayers)
            {
                this.clients.Add(client);
                return;
            }
            else
            {
                // game already full, reject the connection
                try
                {
                    SimpleMessage message = new SimpleMessage(MessageType.Reject, "game already full");
                    NetworkProtocol.Send(client, message);
                }
                catch (SocketException) { }
            }

		}

        // Iterate through clients and check if they have new messages or are disconnected
        int playerIdx = 0;
        foreach (var client in this.clients)
		{
            try
            {
                if (client == null) continue;
                if (this.IsSocketConnected(client) == false)
                {
                    System.Console.WriteLine("Client not connected anymore");
                    this.clientsToRemove.Add(client);
                }
                var messages = NetworkProtocol.Receive(client);
                foreach(SimpleMessage message in messages)
                {
                    System.Console.WriteLine("Received message: " + message.message + " type: " + message.messageType);
                    bool disconnect = HandleMessage(playerIdx, client, message);
                    if (disconnect)
                        this.clientsToRemove.Add(client);
                }
            }
            catch (Exception e)
            {
                System.Console.WriteLine("Error receiving from a client: " + e.Message);
                this.clientsToRemove.Add(client);
            }
            playerIdx++;
		}

        //Remove dead clients
        foreach (var clientToRemove in this.clientsToRemove)
        {
            try
            {
                this.RemoveClient(clientToRemove);
            }
            catch (Exception e)
            {
                System.Console.WriteLine("Couldn't remove client: " + e.Message);
            }
        }
        this.clientsToRemove.Clear();

        //End game if no clients
        if(this.server.GameStarted())
        {
            if(this.clients.Count <= 0)
            {
                System.Console.WriteLine("Clients gone, stop session");
                this.TerminateGameSession();
            }
        }
    }

    public void DisconnectAll()
    {
        // warn clients
        SimpleMessage message = new SimpleMessage(MessageType.Disconnect);
        TransmitMessage(message);
        // disconnect connections
        foreach (var client in this.clients)
        {
            this.clientsToRemove.Add(client);
        }

        //Reset the client lists
        this.clients = new List<TcpClient>();
        this.readyClients = new List<TcpClient>();
	}

    //Transmit message to multiple clients
	private void TransmitMessage(SimpleMessage msg, TcpClient excludeClient = null)
	{
        // send the same message to all players
        foreach (var client in this.clients)
		{
            //Skip if this is the excluded client
            if(excludeClient != null && excludeClient == client)
            {
                continue;
            }

			try
			{
				NetworkProtocol.Send(client, msg);
			}
			catch (Exception e)
			{
                this.clientsToRemove.Add(client);
			}
		}
    }

    //Send message to single client
    private void SendMessage(TcpClient client, SimpleMessage msg)
    {
        try
        {
            NetworkProtocol.Send(client, msg);
        }
        catch (Exception e)
        {
            this.clientsToRemove.Add(client);
        }
    }

    private bool HandleMessage(int playerIdx, TcpClient client, SimpleMessage msg)
	{
        if (msg.messageType == MessageType.Disconnect)
        {
            this.clientsToRemove.Add(client);
            return true;
        }
        else if (msg.messageType == MessageType.Ready)
            HandleReady(client);
        else if (msg.messageType == MessageType.Spawn)
            HandleSpawn(client, msg);
        else if (msg.messageType == MessageType.Position)
            HandlePos(client, msg);

        return false;
    }


	private void HandleReady(TcpClient client)
	{
        // start the game once we have at least one client online
        this.readyClients.Add(client);

        if (readyClients.Count >= 1)
        {
            System.Console.WriteLine("Enough clients, let's start the game!");
            this.server.StartGame();
        }
	}

    private void HandleSpawn(TcpClient client, SimpleMessage message)
    {
        // Get client id (index in list for now)
        int clientId = this.clients.IndexOf(client);

        System.Console.WriteLine("Player " + clientId + " spawned with coordinates: " + message.float1 + "," + message.float2 + "," + message.float3);

        // Add client ID
        message.clientId = clientId;

        // Add to list to create the gameobject instance on the server
        Server.messagesToProcess.Add(message);

        //Inform the other clients about the player pos
        this.TransmitMessage(message, excludeClient: client);

    }

    private void HandlePos(TcpClient client, SimpleMessage message)
    {
        // Get client id (index in list for now)
        int clientId = this.clients.IndexOf(client);

        System.Console.WriteLine("Got pos from client: " + clientId + " with coordinates: " + message.float1 + "," + message.float2 + "," + message.float3);

        // Add client ID
        message.clientId = clientId;

        // Add to list to create the gameobject instance on the service
        Server.messagesToProcess.Add(message);

        // Inform the other clients about the player pos
        // (NOTE: We should validate it's legal and actually share the server view of the position)
        this.TransmitMessage(message, excludeClient: client);
    }

    private void RemoveClient(TcpClient client)
    {
        //Let the other clients know the player was removed
        int clientId = this.clients.IndexOf(client);

        SimpleMessage message = new SimpleMessage(MessageType.PlayerLeft);
        message.clientId = clientId;
        TransmitMessage(message, client);

        // Disconnect and remove
        this.DisconnectPlayer(client);
        this.clients.Remove(client);
        this.readyClients.Remove(client);
    }

	private void DisconnectPlayer(TcpClient client)
	{
        try
        {
            // remove the client and close the connection
            if (client != null)
            {
                NetworkStream stream = client.GetStream();
                stream.Close();
                client.Close();
            }
        }
        catch (Exception e)
        {
            System.Console.WriteLine("Failed to disconnect player: " + e.Message);
        }
	}
}
#endif
