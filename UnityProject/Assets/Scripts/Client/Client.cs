// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using Amazon;
using Amazon.CognitoIdentity;
using Amazon.CognitoIdentityProvider;

// *** MAIN CLIENT CLASS FOR MANAGING CLIENT CONNECTIONS AND MESSAGES ***

public class Client : MonoBehaviour
{
    // Prefabs for the player and enemy objects referenced from the scene object
    public GameObject characterPrefab;
	public GameObject enemyPrefab;

#if CLIENT

    // Local player
    private NetworkClient networkClient;
    private NetworkPlayer localPlayer;

    // List of enemy players
    private List<NetworkPlayer> enemyPlayers = new List<NetworkPlayer>();

    //We get events back from the NetworkServer through this static list
    public static List<SimpleMessage> messagesToProcess = new List<SimpleMessage>();

    //Cognito credentials for sending signed requests to the API
    public static Amazon.Runtime.ImmutableCredentials cognitoCredentials = null;

    // Helper function to check if an enemy exists in the enemy list already
    private bool EnemyPlayerExists(int clientId)
    {
        foreach(NetworkPlayer player in enemyPlayers)
        {
            if(player.GetPlayerId() == clientId)
            {
                return true;
            }
        }
        return false;
    }

    // Helper function to find and enemy from the enemy list
    private NetworkPlayer GetEnemyPlayer(int clientId)
    {
        foreach (NetworkPlayer player in enemyPlayers)
        {
            if (player.GetPlayerId() == clientId)
            {
                return player;
            }
        }
        return null;
    }

    // Called by Unity when the Gameobject is created
    void Start()
    {
        FindObjectOfType<UIManager>().SetTextBox("Setting up Client..");

        // Get an identity and connect to server
        CognitoAWSCredentials credentials = new CognitoAWSCredentials(
            MatchmakingClient.identityPoolID,
            MatchmakingClient.region);
        Client.cognitoCredentials = credentials.GetCredentials();
        Debug.Log("Got credentials: " + Client.cognitoCredentials.AccessKey + "," + Client.cognitoCredentials.SecretKey);

        StartCoroutine(ConnectToServer());
    }

    // Fixed Update is called once physics step which is configured to 30 / seconds
    // We use this same step for client and server to send messages and sync positions
    void FixedUpdate()
    {
        if (this.localPlayer != null)
        {
            // Process any messages we have received over the network
            this.ProcessMessages();

            // Send current Move command
            this.SendMove();

            // Receive new messages
            this.networkClient.Update();
        }
    }

    // Find a game session and connect to the server endpoint received
    // This is a coroutine to simplify the logic and keep our UI updated throughout the process
    IEnumerator ConnectToServer()
    {
        FindObjectOfType<UIManager>().SetTextBox("Connecting to backend..");

        yield return null;

        // Start network client and connect to server
        this.networkClient = new NetworkClient();
        // We will wait for the matchmaking and connection coroutine to end before creating the player
        yield return StartCoroutine(this.networkClient.RequestGameSession());

        if (this.networkClient.ConnectionSucceeded())
        {
            // Create character
            this.localPlayer = new NetworkPlayer(0);
            this.localPlayer.Initialize(characterPrefab, new Vector3(UnityEngine.Random.Range(-5,5), 1, UnityEngine.Random.Range(-5, 5)));
            this.localPlayer.ResetTarget();
            this.networkClient.SendMessage(this.localPlayer.GetSpawnMessage());
        }

        yield return null;
    }

    // Process messages received from server
    void ProcessMessages()
    {
        List<int> justLeftClients = new List<int>();
        List<int> clientsMoved = new List<int>();

        // Go through any messages to process
        foreach (SimpleMessage msg in messagesToProcess)
        {
            // Own position
            if(msg.messageType == MessageType.PositionOwn)
            {
                this.localPlayer.ReceivePosition(msg, this.characterPrefab);
            }
            // players spawn and position messages
            else if (msg.messageType == MessageType.Spawn || msg.messageType == MessageType.Position || msg.messageType == MessageType.PlayerLeft)
            {
                if (msg.messageType == MessageType.Spawn && this.EnemyPlayerExists(msg.clientId) == false)
                {
                    Debug.Log("Enemy spawned: " + msg.float1 + "," + msg.float2 + "," + msg.float3 + " ID: " + msg.clientId);
                    NetworkPlayer enemyPlayer = new NetworkPlayer(msg.clientId);
                    this.enemyPlayers.Add(enemyPlayer);
                    enemyPlayer.Spawn(msg, this.enemyPrefab);
                }
                else if (msg.messageType == MessageType.Position && justLeftClients.Contains(msg.clientId) == false)
                {
                    Debug.Log("Enemy pos received: " + msg.float1 + "," + msg.float2 + "," + msg.float3);
                    //Setup enemycharacter if not done yet
                    if (this.EnemyPlayerExists(msg.clientId) == false)
                    {
                        Debug.Log("Creating new with ID: " + msg.clientId);
                        NetworkPlayer newPlayer = new NetworkPlayer(msg.clientId);
                        this.enemyPlayers.Add(newPlayer);
                        newPlayer.Spawn(msg, this.enemyPrefab);
                    }
                    // We pass the prefab with the position message as it might be the enemy is not spawned yet
                    NetworkPlayer enemyPlayer = this.GetEnemyPlayer(msg.clientId);
                    enemyPlayer.ReceivePosition(msg, this.enemyPrefab);

                    clientsMoved.Add(msg.clientId);
                }
                else if(msg.messageType == MessageType.PlayerLeft)
                {
                    Debug.Log("Player left " + msg.clientId);
                    // A player left, remove from list and delete gameobject
                    NetworkPlayer enemyPlayer = this.GetEnemyPlayer(msg.clientId);
                    if(enemyPlayer != null)
                    {
                        Debug.Log("Found enemy player");
                        enemyPlayer.DeleteGameObject();
                        this.enemyPlayers.Remove(enemyPlayer);
                        justLeftClients.Add(msg.clientId);
                    }
                }
            }
        }
        messagesToProcess.Clear();

        // Interpolate all enemy players towards their current target
        foreach (var enemyPlayer in this.enemyPlayers)
        {
            enemyPlayer.InterpolateToTarget();
        }

        // Interpolate player towards his/her current target
        this.localPlayer.InterpolateToTarget();
    }

    void SendMove()
    {
        // Send position if changed
        var newPosMessage = this.localPlayer.GetMoveMessage();
        if (newPosMessage != null)
            this.networkClient.SendMessage(newPosMessage);
    }
#endif
}