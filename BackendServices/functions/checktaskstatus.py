import json
import datetime
import time
import os
import boto3
from boto3.dynamodb.conditions import Key, Attr
import redis
from datetime import timedelta

# Checks if all game servers in a Task are done hosting maximum amount of game sessions

def lambda_handler(event, context):

    # Get redis endpoint and set up
    redis_endpoint = os.environ['REDIS_ENDPOINT']
    # Setup Redis client
    redis_client = redis.Redis(host=redis_endpoint, port=6379, db=0)

    # Get the parameters from the server
    taskArn = event["taskArn"]

    print("taskArn: " + str(taskArn))

    # Get all game servers within this task in any state (active, full, available)
    # Servers that are already done should be already removed from Redis completely
    active_game_servers_in_task_response = redis_client.scan(count=100,match="*-gameserver-"+taskArn+"*")
    active_game_servers_in_task_count = len(active_game_servers_in_task_response[1])
    print("game servers still active: " + str(active_game_servers_in_task_count))

    if active_game_servers_in_task_count <= 0:
        print("No game servers in the Task, return true")
        return True

    print("Game servers still active in Task, return false")
    return False