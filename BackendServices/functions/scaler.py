import json
import datetime
import time
import os
import boto3
from boto3.dynamodb.conditions import Key, Attr
import redis
from datetime import timedelta

# Containers in a single Task (this is configured also outside of the Lambda so update as needed!)
containers_in_task = 10

# We want to keep at least X available game servers running
total_game_servers_target_min = 30

# Hard limit for max amount of servers to start at once to avoid hitting API throttling (this is containers not Tasks so with 10 containers per task, max of 30 would be 3 Tasks)
max_game_servers_to_start = 30

# We want to keep 20% excess capacity to current load
available_game_servers_target_percentage = 0.2

# Max players in a game session
max_players = 2

# The amount of seconds we give servers to start up
server_startup_grace_period = 60

def lambda_handler(event, context):

    print("Running scheduled Lambda function to start new game server tasks when necessary")

    # Get the resources from ECS and Task CLoudFormation Stacks from environment variables
    fargate_cluster_name = os.environ['FARGATE_CLUSTER_NAME'] 
    subnet1 = os.environ['SUBNET_1']
    subnet2 = os.environ['SUBNET_2']
    fargate_task_definition = ""
    security_group = os.environ['SECURITY_GROUP']
    redis_endpoint = os.environ['REDIS_ENDPOINT']

    cloudformation = boto3.client("cloudformation")
    # Get the Task to deploy (as this changes dynamically)
    stack = cloudformation.describe_stacks(StackName="fargate-game-servers-task-definition")["Stacks"][0]
    for output in stack["Outputs"]:
        print('%s=%s (%s)' % (output["OutputKey"], output["OutputValue"], output["Description"]))
        if output["OutputKey"] == "TaskDefinition":
            fargate_task_definition = output["OutputValue"]

    # Setup Redis client
    redis_client = redis.Redis(host=redis_endpoint, port=6379, db=0)

    # Track start time
    start_time = time.time()

    ### Run the scaler up to 60 seconds (next one will be triggered after 1 minute)
    while (time.time() - start_time) < 58.0:

        try:

            #  Get Task count in the Cluster for reference
            #  We will use this to detect failing builds that don't report correctly back to Redis
            #  Using Pagination to get all Tasks (even over 100)
            ecs = boto3.client("ecs")
            task_count = 0
            response = ecs.list_tasks(cluster=fargate_cluster_name,launchType='FARGATE')
            task_count += len(response["taskArns"])
            while "nextToken" in response:
                    response = ecs.list_tasks(cluster=fargate_cluster_name,launchType='FARGATE', nextToken=response["nextToken"])
                    task_count += len(response["taskArns"])
            expected_amount_of_game_servers = task_count * containers_in_task
            print("Tasks running currently: " + str(task_count) + " Expecting game server count: " + str(expected_amount_of_game_servers))

            # 1. Get the available priority, available, active and full servers (support up to 100k each) to calculate total sum
            available_game_servers_response = redis_client.scan(count=100000,match="available-gameserver-*")
            available_game_servers = len(available_game_servers_response[1])
            available_priority_game_servers_response = redis_client.scan(count=100000,match="available-priority-gameserver-*")
            available_priority_game_servers = len(available_priority_game_servers_response[1])
            print("{ \"Available_priority_game_servers\" : \"" + str(available_priority_game_servers) + "\" }")
            print("{ \"Available_game_servers\" : \"" + str(available_game_servers + available_priority_game_servers) + "\" }")
            active_game_servers_response = redis_client.scan(count=100000,match="active-gameserver-*")
            active_game_servers = len(active_game_servers_response[1])
            print("{ \"Active_game_servers\" : \"" + str(active_game_servers) + "\" }")
            full_game_servers_response = redis_client.scan(count=100000,match="full-gameserver-*")
            full_game_servers = len(full_game_servers_response[1])
            print("{ \"Full_game_servers\" : \"" + str(full_game_servers) + "\" }")

            total_game_servers = available_game_servers + available_priority_game_servers + active_game_servers + full_game_servers

            print("{ \"Total_game_servers\" : \"" + str(total_game_servers) + "\" }")

            # If there's triple the amount of Tasks compared to registered game servers,
            # we can safely say there's an issue in the game servers (not reporting to Redis)
            # In this case we skip any new starts
            if expected_amount_of_game_servers > (total_game_servers * 3):
                print("ERROR: We are running over triple the amount of containers compared to registered game servers. Server Build is clearly broken.");
                print("WILL NOT START NEW GAME SERVERS TO AVOID COST OVERLOAD!")
                time.sleep(1)
                continue

            # Calculate the 0-1 percentage value of available game servers
            percentage_available = 0.0
            if total_game_servers > 0:
                percentage_available = float(available_game_servers + available_priority_game_servers) / float(total_game_servers)
            print("{ \"Percentage_available\" : \"" + str(percentage_available) + "\" }")

            # Spin up the missing servers and make sure we have at least minimum
            if percentage_available < available_game_servers_target_percentage or total_game_servers < total_game_servers_target_min:
                amount_to_start = int((available_game_servers_target_percentage - percentage_available) * total_game_servers)
                print("planning to start game servers amount:" + str(amount_to_start))
                # Make sure we have minimum of 1 server started as low capacity was identified
                if amount_to_start == 0:
                    amount_to_start = 1
                    print("clamping value to minimum of 1 started game servers")
                # Make sure we have the baseline at least running
                if total_game_servers < total_game_servers_target_min:
                    amount_to_start = total_game_servers_target_min - total_game_servers
                    print("setting amount to start to get minimum baseline amount running: " + str(amount_to_start))

                # Don't start more than our hard limit
                if amount_to_start > max_game_servers_to_start:
                    amount_to_start = max_game_servers_to_start
                    print("limiting to max game servers to start on a single update hard limit: " + str(amount_to_start))

                # Divide amount to start with the amount of containers we have in a single Task
                was_more_than_zero = amount_to_start > 0
                amount_to_start  = int(amount_to_start / containers_in_task)
                print("Divided by the amount of containers we know to be in a single task: " + str(amount_to_start))

                if amount_to_start == 0 and was_more_than_zero:
                    print("Starting at least one Task as we needed one more game server")
                    amount_to_start = 1

                # Start a game server Fargate Task for each missing game server in batches of 10 (default soft limit, a quota increase can be requested)
                rounds = int(amount_to_start / 10) + 1
                print("Starting " + str(amount_to_start) + " Tasks in " + str(rounds) + " rounds")
                for i in range(rounds):
                    start_this_round = 10
                    # Last round we start the remaining tasks
                    if i == rounds-1:
                        start_this_round = amount_to_start % 10
                    print("Starting " + str(start_this_round) + " Tasks")
                    if start_this_round > 0:
                        client = boto3.client('ecs')
                        response = client.run_task(
                            cluster=fargate_cluster_name,
                            launchType = 'FARGATE',
                            taskDefinition=fargate_task_definition,
                            count = start_this_round,
                            platformVersion='1.4.0',
                            networkConfiguration={
                                'awsvpcConfiguration': {
                                    'subnets': [
                                        subnet1,
                                        subnet2
                                    ],
                                    'assignPublicIp': 'ENABLED',
                                    'securityGroups': [
                                        security_group
                                    ],
                                }
                            }
                        )
                        # Extract Task info from response and prepopulate Redis to match the capacity (game servers will take over after this)
                        for task in response["tasks"]:
                            #print(task)
                            # Add all the containers in Task as individual game servers
                            for i in range(0,len(task["containers"])):
                                container_key = "available-gameserver-"+task["taskArn"]+"-container"+str(i);
                                redis_client.hset(container_key, "server-id", task["taskArn"]+"-container"+str(i))
                                redis_client.hset(container_key, "current-players", 0)
                                redis_client.hset(container_key, "max-players", max_players)
                                redis_client.hset(container_key, "ready", 0) #Server will define itself ready when it's started
                                # Expire in 60 seconds (wait for server to start up)
                                redis_client.expire(container_key, timedelta(seconds=server_startup_grace_period))
        except:
            print("Exception occured in starting Tasks")
        # Wait for next round unless this was the last on this minute
        if time.time() - start_time < 58.0:
            print("Wait 2 seconds before next round")
            time.sleep(2.0)