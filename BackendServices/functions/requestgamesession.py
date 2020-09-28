import json
import datetime
import time
import os
import boto3
from boto3.dynamodb.conditions import Key, Attr
import redis
from datetime import timedelta
import random
from redis.exceptions import (
    ConnectionError,
    DataError,
    ExecAbortError,
    NoScriptError,
    PubSubError,
    RedisError,
    ResponseError,
    TimeoutError,
    WatchError,
)

def lambda_handler(event, context):

    # Get redis endpoint and set up
    redis_endpoint = os.environ['REDIS_ENDPOINT']
    # Setup Redis client
    redis_client = redis.Redis(host=redis_endpoint, port=6379, db=0)

    # 1. Check if there are game servers that have players in them but are not full yet    
    print("Checking if there are active servers with players already")
    active_game_servers_response = redis_client.scan(count=100000,match="active-gameserver-*")
    if len(active_game_servers_response[1]) > 0:
        print("Found an active game server, trying to take the spot")

        # Try to claim a spot on a random active game server. Use WATCH locking to make sure no-one else does the same
        with redis_client.pipeline() as pipe:
            # We'll try this 25 times and then just fail
            for x in range(25):
                try:
                    # Take a random active game server
                    game_server_key = random.choice(active_game_servers_response[1])

                    # put a WATCH on a lock for this specific key
                    pipe.watch(b'-lock'+game_server_key)

                    # get the current reservation and max players for this game server. Server will use "current-players" for actually connected players
                    current_reservations = pipe.hget(game_server_key, b'reserved-player-slots')
                    max_players = pipe.hget(game_server_key, b'max-players')
                    print("current reservations: " + str(current_reservations))
                    print("max players: " + str(max_players))

                    if current_reservations == None:
                        current_reservations = 0

                    # Check if this was preserved full already
                    if int(current_reservations) >= int(max_players):
                        print("Server full, cannot join")
                        continue

                    next_value = int(current_reservations) + 1

                    # now we can put the pipeline back into buffered mode with MULTI
                    pipe.multi()
                    pipe.hset(game_server_key, b'reserved-player-slots', next_value)
                    pipe.hset(game_server_key, b'last-reservation-time', time.time())
                    # Update lock
                    pipe.set(b'-lock'+game_server_key, "")
                    pipe.expire(b'-lock'+game_server_key, timedelta(seconds=3))
                    # and finally, execute the pipeline (the set command)
                    pipe.execute()

                    # If we reached here, there was no WatchError
                    print("Successfully taken the spot, return IP and port to client")

                    publicIP = redis_client.hget(game_server_key, b'publicIP')
                    port = redis_client.hget(game_server_key, b'port')

                    print("Got server: " + str(publicIP) + ":" + str(port))

                    return {
                        "statusCode": 200,
                        "body": json.dumps({ 'publicIP': publicIP.decode('UTF-8'), 'port': port.decode('UTF-8') })
                    }
                except WatchError:
                    # another client must have changed 'OUR-SEQUENCE-KEY' between
                    # the time we started WATCHing it and the pipeline's execution.
                    # our best bet is to just retry.
                    print("Failed to reserve slot, retrying")


    # 2. As no game servers with players in them found, search for a free available game server
    # We'll try this 30 times and then just fail
    for x in range(30):
        try:
            #   Check priority list first for the first 20 rounds (Game servers on Tasks that already hosted sessions) for good rotation of Tasks
            #   For the last 10 rounds we switch to non-priority to make sure any issues in priority won't fail us completely
            available_game_servers_response = None
            if x < 20:
                print("No active game sessions, checking priority servers first from available")
                available_game_servers_response = redis_client.scan(count=100000,match="available-priority-gameserver-*")

            if available_game_servers_response == None or len(available_game_servers_response[1]) == 0:
                # No priority servers, check list of servers on fresh Tasks
                print("No priority servers. Checking if there are available servers with no players")
                available_game_servers_response = redis_client.scan(count=100000,match="available-gameserver-*")

            if len(available_game_servers_response[1]) > 0:
                print("Found an available game server, trying to take the spot")
                #print(available_game_servers_response[1])
                #server_info = redis_client.hgetall(available_game_servers_response[1][0])
                #print(server_info)

            # Try to claim a spot on a random available server, use WATCH locking to make sure no-one else does the same
            with redis_client.pipeline() as pipe:

                # Take a random available game server
                game_server_key = random.choice(available_game_servers_response[1])

                # Make sure the server is ready to accept connections
                server_ready = redis_client.hget(game_server_key, b'ready')
                print("Server ready: " + str(server_ready))
                if int(server_ready) == 0:
                    print("Server not ready yet, retry.")
                    continue

                # put a WATCH on a lock for this specific key
                pipe.watch(b'-lock'+game_server_key)

                # get the current reservation and max players for this game server. Server will use "current-players" for actually connected players
                current_reservations = pipe.hget(game_server_key, b'reserved-player-slots')
                max_players = pipe.hget(game_server_key, b'max-players')
                print("current reservations: " + str(current_reservations))
                print("max players: " + str(max_players))

                if current_reservations == None:
                    current_reservations = 0

                # Check if this was preserved full already
                if int(current_reservations) >= int(max_players):
                    print("Server full, cannot join")
                    continue

                next_value = int(current_reservations) + 1

                # now we can put the pipeline back into buffered mode with MULTI
                pipe.multi()
                pipe.hset(game_server_key, b'reserved-player-slots', next_value)
                pipe.hset(game_server_key, b'last-reservation-time', time.time())
                # Update lock
                pipe.set(b'-lock'+game_server_key, "")
                pipe.expire(b'-lock'+game_server_key, timedelta(seconds=3))
                # and finally, execute the pipeline (the set command)
                pipe.execute()

                # If we reached here, there was no WatchError
                print("Successfully taken the spot, return IP and port to client")

                publicIP = redis_client.hget(game_server_key, b'publicIP')
                port = redis_client.hget(game_server_key, b'port')

                print("Got server: " + str(publicIP) + ":" + str(port))

                return {
                    "statusCode": 200,
                    "body": json.dumps({ 'publicIP': publicIP.decode('UTF-8'), 'port': port.decode('UTF-8') })
                }
        except WatchError:
            # another client must have changed 'OUR-SEQUENCE-KEY' between
            # the time we started WATCHing it and the pipeline's execution.
            # our best bet is to just retry.
            print("Failed to reserve slot, retrying")

    # Failed to find a server
    return {
            "statusCode": 500,
            "body": json.dumps({ 'failed': 'couldnt find a free server spot'})
    }