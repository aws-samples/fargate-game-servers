// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Net.Sockets;
using UnityEngine;
using System.Collections;

#if CLIENT

// *** NETWORK CLIENT FOR TCP CONNECTIONS WITH THE SERVER ***

public class NetworkClient
{
    private MatchmakingClient matchmakingClient;

	private TcpClient client = null;

	private bool connectionSucceeded = false;

    private GameSessionInfo gameSessionInfo = null;

    public bool ConnectionSucceeded() { return connectionSucceeded; }

    public NetworkClient()
    {
        this.matchmakingClient = new MatchmakingClient();
    }

	public IEnumerator RequestGameSession()
	{
		Debug.Log("Request matchmaking...");
		GameObject.FindObjectOfType<UIManager>().SetTextBox("Requesting game session...");
		yield return null;

		bool gameSessionFound = false;
		int tries = 0;
        while (!gameSessionFound)
        {
			GameObject.FindObjectOfType<UIManager>().SetTextBox("Requesting game session...");
			yield return null;
			this.gameSessionInfo = this.matchmakingClient.RequestGameSession();
			if (gameSessionInfo == null)
			{
				GameObject.FindObjectOfType<UIManager>().SetTextBox("No game session found yet, trying again...");
				yield return new WaitForSeconds(1.0f);
			}
			else
			{
				Debug.Log("Got session: " + gameSessionInfo.publicIP + " " + gameSessionInfo.port);
				GameObject.FindObjectOfType<UIManager>().SetTextBox("Found a game server, connecting...");
				yield return null;

				gameSessionFound = true;
				// game session found, connect to the server
				Connect();
			}
			tries++;

			if(tries > 20)
            {
				GameObject.FindObjectOfType<UIManager>().SetTextBox("Aborting game session search, no game found in 20 seconds");
				Debug.Log("Aborting game session search, no game found in 20 seconds");
				yield return null;
				break;
			}
		}
	}

	// Called by the client to receive new messages
	public void Update()
	{
		if (client == null) return;
		var messages = NetworkProtocol.Receive(client);
        
		foreach (SimpleMessage msg in messages)
		{
			HandleMessage(msg);
		}
	}

	private bool TryConnect()
	{
		try
		{
			//Connect with matchmaking info
			Debug.Log("Connect..");
			this.client = new TcpClient();
			var result = client.BeginConnect(this.gameSessionInfo.publicIP, this.gameSessionInfo.port, null, null);

			var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));

			if (!success)
			{
				throw new Exception("Failed to connect.");
			}
            client.NoDelay = true; // Use No Delay to send small messages immediately. UDP should be used for even faster messaging
			Debug.Log("Done");

			return true;
		}
		catch (Exception e)
		{
			Debug.Log(e.Message);
			client = null;
			return false;
		}
	}

	public void Connect()
	{
		// try to connect to a local server
		if (TryConnect() == false)
		{
			Debug.Log("Failed to connect to server");
			GameObject.FindObjectOfType<UIManager>().SetTextBox("Connection to server failed.");
		}
		else
		{
			//We're ready to play, let the server know
			this.Ready();
			GameObject.FindObjectOfType<UIManager>().SetTextBox("Connected to server");
		}
	}

	// Send ready to play message to server
	public void Ready()
	{
		if (client == null) return;
		this.connectionSucceeded = true;

        // Send READY message to let server know we are ready
        SimpleMessage message = new SimpleMessage(MessageType.Ready);
		try
		{
			NetworkProtocol.Send(client, message);
		}
		catch (SocketException e)
		{
			HandleDisconnect();
		}
	}

    // Send serialized binary message to server
    public void SendMessage(SimpleMessage message)
    {
        if (client == null) return;
        try
        {
            NetworkProtocol.Send(client, message);
        }
        catch (SocketException e)
        {
            HandleDisconnect();
        }
    }

	// Send disconnect message to server
	public void Disconnect()
	{
		if (client == null) return;
        SimpleMessage message = new SimpleMessage(MessageType.Disconnect);
		try
		{
			NetworkProtocol.Send(client, message);
		}

		finally
		{
			HandleDisconnect();
		}
	}

	// Handle a message received from the server
	private void HandleMessage(SimpleMessage msg)
	{
		// parse message and pass json string to relevant handler for deserialization
		Debug.Log("Message received:" + msg.messageType + ":" + msg.message);

		if (msg.messageType == MessageType.Reject)
			HandleReject();
		else if (msg.messageType == MessageType.Disconnect)
			HandleDisconnect();
		else if (msg.messageType == MessageType.Spawn)
			HandleOtherPlayerSpawned(msg);
		else if (msg.messageType == MessageType.Position)
			HandleOtherPlayerPos(msg);
		else if (msg.messageType == MessageType.PlayerLeft)
			HandleOtherPlayerLeft(msg);
	}

	private void HandleReject()
	{
		NetworkStream stream = client.GetStream();
		stream.Close();
		client.Close();
		client = null;
	}

	private void HandleDisconnect()
	{
		Debug.Log("Got disconnected by server");
		GameObject.FindObjectOfType<UIManager>().SetTextBox("Got disconnected by server");
		NetworkStream stream = client.GetStream();
		stream.Close();
		client.Close();
		client = null;
	}

	private void HandleOtherPlayerSpawned(SimpleMessage message)
	{
		Client.messagesToProcess.Add(message);
	}

	private void HandleOtherPlayerPos(SimpleMessage message)
    {
		Client.messagesToProcess.Add(message);
	}

	private void HandleOtherPlayerLeft(SimpleMessage message)
	{
		Client.messagesToProcess.Add(message);
	}
}

#endif

