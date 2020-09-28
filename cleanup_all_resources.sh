#!/bin/bash -e

# Get the configuration variables
source configuration.sh

read -p "Are you sure you want to delete all resources? " -n 1 -r
echo    # (optional) move to a new line
if [[ ! $REPLY =~ ^[Yy]$ ]]
then
    exit 1
fi

echo "Delete Cognito Stack.."
aws cloudformation --region $region delete-stack --stack-name fargate-game-servers-cognito
aws cloudformation --region $region wait stack-delete-complete --stack-name fargate-game-servers-cognito
echo "Done deleting stack!"

echo "Delete Backend Services Stack.."
aws cloudformation --region $region delete-stack --stack-name fargate-game-servers-backend
aws cloudformation --region $region wait stack-delete-complete --stack-name fargate-game-servers-backend
echo "Done deleting stack!"

echo "Delete Task Definition Stack.."
aws cloudformation --region $region delete-stack --stack-name fargate-game-servers-task-definition
aws cloudformation --region $region wait stack-delete-complete --stack-name fargate-game-servers-task-definition
echo "Done deleting stack!"

echo "Delete Redis Stack.."
aws cloudformation --region $region delete-stack --stack-name fargate-game-servers-elasticache-redis
aws cloudformation --region $region wait stack-delete-complete --stack-name fargate-game-servers-elasticache-redis
echo "Done deleting stack!"

read -p "ACTION REQUIRED: Go to the ECS Cluster Tasks in AWS Management Console and STOP ALL TASKS. Once ready, press any key. " -n 1 -r
echo    # (optional) move to a new line

echo "Delete VPC and ECS Stack.."
aws cloudformation --region $region delete-stack --stack-name fargate-game-servers-ecs-resources
aws cloudformation --region $region wait stack-delete-complete --stack-name fargate-game-servers-ecs-resources
echo "Done deleting stack!"

echo "All Resources cleaned up!"