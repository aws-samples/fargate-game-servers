import json
import datetime
import time
import os
import boto3
from boto3.dynamodb.conditions import Key, Attr
import redis
from datetime import timedelta


def lambda_handler(event, context):

    # The TTL in Redis for game server data. We expect updates every 15 seconds from game servers so leave 5 seconds headroom
    gameserverdata_ttl = 20.0

    # Get redis endpoint and set up
    redis_endpoint = os.environ['REDIS_ENDPOINT']
    # Setup Redis client
    redis_client = redis.Redis(host=redis_endpoint, port=6379, db=0)

    # Get the parameters from the server
    server_in_use = event["serverInUse"]
    taskArn = event["taskArn"] # This includes the container (taskarn-containerX)
    current_players = event["currentPlayers"]
    max_players = event["maxPlayers"]
    ready = event["ready"]
    publicIP = event["publicIP"]
    port = event["port"]
    serverTerminated = event["serverTerminated"]

    # Get only the Task arn (it includes the container)
    onlyTaskArn = taskArn.split("-container")[0]

    print("server_in_use: " + str(server_in_use))
    print("taskArn with container: " + str(taskArn))
    print("Only TaskArn: " + str(onlyTaskArn))
    print("current_players: " + str(current_players))
    print("max_players: " + str(max_players))
    print("ready: " + str(ready))
    print("publicIP: " + str(publicIP))
    print("port: " + str(port))
    print("serverTerminated: " + str(serverTerminated))

    # Check for outdated player session reservations
    # Get the last reservation time and check against current time. It could be active, available or available_priority
    game_server_key = "available-gameserver-"+taskArn
    last_reservation_time = redis_client.hget(game_server_key, "last-reservation-time")
    current_reservations = None
    if last_reservation_time == None:
        game_server_key = "available-priority-gameserver-"+taskArn
        last_reservation_time = redis_client.hget(game_server_key, "last-reservation-time")
    if last_reservation_time == None:
        game_server_key = "active-gameserver-"+taskArn
        last_reservation_time = redis_client.hget(game_server_key, "last-reservation-time")
    if last_reservation_time != None:
        print("Found last reservation time: " + str(last_reservation_time))
        timedifference = time.time() - float(last_reservation_time)
        print("time since last reservation: " + str(timedifference))
        current_reservations = redis_client.hget(game_server_key, "reserved-player-slots")
        # If 30s passed, clamp reservations to currentplayers --> update with hset
        if timedifference > 30.0:
            print("Clear outdated reservations")
            if current_reservations != None:
                print("Found current reservations: " + str(current_reservations))
                if int(current_reservations) > current_players:
                    current_reservations = current_players
                    print("Limiting current reservations to current players")
                    redis_client.hset(game_server_key, "reserved-player-slots", current_reservations)

    if current_reservations == None:
        current_reservations = 0

    # Convert booleans to int for Redis
    server_in_use = int(server_in_use == True)
    ready = int(ready == True)

    if serverTerminated:
        print("Server terminated, delete entry and quit")
        # Delete all the possible gameserver keys
        redis_client.delete("available-gameserver-"+taskArn)
        redis_client.delete("active-gameserver-"+taskArn)
        redis_client.delete("full-gameserver-"+taskArn)
        return

    if publicIP == None:
        print("Public IP not set, can't update server in Redis")
        return

    # 1. If server is in use, delete from available servers and update to in use servers
    if server_in_use == 1:
        print("marking server as in use")
        # Delete the possible other gameserver key
        redis_client.delete("available-gameserver-"+taskArn)
        redis_client.delete("available-priority-gameserver-"+taskArn)
        redis_client.delete("active-gameserver-"+taskArn)
        # Update the full game server info
        redis_client.hset("full-gameserver-"+taskArn, "server-id", taskArn)
        redis_client.hset("full-gameserver-"+taskArn, "current-players", current_players)
        redis_client.hset("full-gameserver-"+taskArn, "max-players", max_players)
        redis_client.hset("full-gameserver-"+taskArn, "ready", ready) #Server will define itself ready when it's started
        redis_client.hset("full-gameserver-"+taskArn, "publicIP", publicIP)
        redis_client.hset("full-gameserver-"+taskArn, "port", port)
        # Expire in gameserverdata_ttl seconds
        redis_client.expire("full-gameserver-"+taskArn, timedelta(seconds=gameserverdata_ttl))
        
        # Mark the whole Task this server is running on as priority
        # (to make sure we prioritize servers that have already hosted sessions for good rotation)
        redis_client.set("prioritize-"+onlyTaskArn, "yes")
        redis_client.expire("prioritize-"+onlyTaskArn, timedelta(seconds=gameserverdata_ttl))

    # 2. If there's someone playing already, add to the active servers (these are used when searching for games)
    elif current_players > 0:
        print("marking server as active (at least one player connected)")
        # Delete the possible other gameserver keys
        redis_client.delete("available-gameserver-"+taskArn)
        redis_client.delete("available-priority-gameserver-"+taskArn)
        redis_client.delete("full-gameserver-"+taskArn)
        # Update the full game server info
        redis_client.hset("active-gameserver-"+taskArn, "server-id", taskArn)
        redis_client.hset("active-gameserver-"+taskArn, "current-players", current_players)
        redis_client.hset("active-gameserver-"+taskArn, "max-players", max_players)
        redis_client.hset("active-gameserver-"+taskArn, "ready", ready) #Server will define itself ready when it's started
        redis_client.hset("active-gameserver-"+taskArn, "publicIP", publicIP)
        redis_client.hset("active-gameserver-"+taskArn, "port", port)
        # Update last reservation time and reservations as well if we found one as it might be we moved from available to active
        if last_reservation_time != None:
            redis_client.hset("active-gameserver-"+taskArn, "last-reservation-time", last_reservation_time)
            redis_client.hset("active-gameserver-"+taskArn, "reserved-player-slots", current_reservations)
        # Expire in gameserverdata_ttlseconds
        redis_client.expire("active-gameserver-"+taskArn, timedelta(seconds=gameserverdata_ttl))

        # Mark the whole Task this server is running on as priority
        # (to make sure we prioritize servers that have already hosted sessions for good rotation)
        redis_client.set("prioritize-"+onlyTaskArn, "yes")
        redis_client.expire("prioritize-"+onlyTaskArn, timedelta(seconds=gameserverdata_ttl))

    # 3. if server is available and no players, delete from full and active servers and set current status based on parameters
    else:
        print("marking server available")

        available_prefix = "available-gameserver-"

        # Check if we should mark this priority (Task already hosted other game sessions)
        priority = redis_client.get("prioritize-"+onlyTaskArn)
        print("priority " + str(priority))
        if redis_client.get("prioritize-"+onlyTaskArn) != None:
            print("This server should be marked priority")
            # Delete the possible other available key as it will never be used again once the Task is priority
            redis_client.delete("available-gameserver-"+taskArn)
            available_prefix = "available-priority-gameserver-"
            # Update priority key so it doesn't expire
            redis_client.set("prioritize-"+onlyTaskArn, "yes")
            redis_client.expire("prioritize-"+onlyTaskArn, timedelta(seconds=gameserverdata_ttl))

        # Delete the possible other gameserver keys
        redis_client.delete("full-gameserver-"+taskArn)
        redis_client.delete("active-gameserver-"+taskArn)
        # Update the available game server info
        redis_client.hset(available_prefix+taskArn, "server-id", taskArn)
        redis_client.hset(available_prefix+taskArn, "current-players", current_players)
        redis_client.hset(available_prefix+taskArn, "max-players", max_players)
        redis_client.hset(available_prefix+taskArn, "ready", ready) #Server will define itself ready when it's started
        redis_client.hset(available_prefix+taskArn, "publicIP", publicIP)
        redis_client.hset(available_prefix+taskArn, "port", port)
        # Update last reservation time and reservations as well if we found one to move this information to the priority entry
        if last_reservation_time != None:
            redis_client.hset(available_prefix+taskArn, "last-reservation-time", last_reservation_time)
            redis_client.hset(available_prefix+taskArn, "reserved-player-slots", current_reservations)
        # Expire in gameserverdata_ttl seconds
        redis_client.expire(available_prefix+taskArn, timedelta(seconds=gameserverdata_ttl))