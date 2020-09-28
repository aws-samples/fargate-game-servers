#!/bin/bash

# Get the configuration variables
source ../configuration.sh

# Returns the status of a stack
getstatusofstack() {
	aws cloudformation describe-stacks --region $region --stack-name $1 --query Stacks[].StackStatus --output text 2>/dev/null
}

# Deploy the VPC and ECS resources with CloudFromation
stackstatus=$(getstatusofstack fargate-game-servers-ecs-resources)
if [ -z "$stackstatus" ]; then
  echo "Creating ecs-resources stack (this will take some time)..."
  aws cloudformation --region $region create-stack --stack-name fargate-game-servers-ecs-resources \
      --template-body file://ecs-resources.yaml \
      --capabilities CAPABILITY_IAM
  aws cloudformation --region $region wait stack-create-complete --stack-name fargate-game-servers-ecs-resources
  echo "Done creating stack!"
else
  echo "Updating ecs-resources stack (this will take some time)..."
  aws cloudformation --region $region update-stack --stack-name fargate-game-servers-ecs-resources \
     --template-body file://ecs-resources.yaml \
     --capabilities CAPABILITY_IAM
  aws cloudformation --region $region wait stack-update-complete --stack-name fargate-game-servers-ecs-resources
  echo "Done updating stack!"
fi

# Deploy the Redis resources with CloudFromation
stackstatus=$(getstatusofstack fargate-game-servers-elasticache-redis)
if [ -z "$stackstatus" ]; then
  echo "Creating elasticache stack (this will take some time)..."
  aws cloudformation --region $region create-stack --stack-name fargate-game-servers-elasticache-redis \
      --template-body file://elasticache-redis.yaml \
      --capabilities CAPABILITY_IAM
  aws cloudformation --region $region wait stack-create-complete --stack-name fargate-game-servers-elasticache-redis
  echo "Done creating stack!"
else
  echo "Updating elasticache stack (this will take some time)..."
  aws cloudformation --region $region update-stack --stack-name fargate-game-servers-elasticache-redis \
     --template-body file://elasticache-redis.yaml \
     --capabilities CAPABILITY_IAM
  aws cloudformation --region $region wait stack-update-complete --stack-name fargate-game-servers-elasticache-redis
  echo "Done updating stack!"
fi