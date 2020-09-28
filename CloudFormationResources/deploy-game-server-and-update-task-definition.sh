#!/bin/bash

# Get the configuration variables
source ../configuration.sh

# Returns the status of a stack
getstatusofstack() {
	aws cloudformation describe-stacks --region $region --stack-name $1 --query Stacks[].StackStatus --output text 2>/dev/null
}

# 1. Create ECR repository if it doesn't exits
aws ecr create-repository --repository-name fargate-game-servers --region $region

# 2. Login to ECR (AWS CLI V2)
aws ecr get-login-password --region $region | docker login --username AWS --password-stdin $accountid.dkr.ecr.$region.amazonaws.com/fargate-game-servers
#eval $(aws ecr get-login --region $region --no-include-email) #This if for CLI V1

# 3. Create Docker Image from latest build (expected to be already created from Unity)
build_id=$(date +%Y-%m-%d.%H%M%S)
docker build ../LinuxServerBuild/ -t $accountid.dkr.ecr.$region.amazonaws.com/fargate-game-servers:$build_id

# 4. Push the image to ECR
docker push $accountid.dkr.ecr.$region.amazonaws.com/fargate-game-servers:$build_id

# 5. Deploy an updated task definition with the new image
stackstatus=$(getstatusofstack fargate-game-servers-task-definition)
if [ -z "$stackstatus" ]; then
  echo "Creating fargate-game-servers-task-definition stack (this will take some time)..."
  aws cloudformation --region $region create-stack --stack-name fargate-game-servers-task-definition \
      --template-body file://game-server-task-definition.yaml \
      --parameters ParameterKey=Image,ParameterValue=$accountid.dkr.ecr.$region.amazonaws.com/fargate-game-servers:$build_id \
      --capabilities CAPABILITY_IAM
  aws cloudformation --region $region wait stack-create-complete --stack-name fargate-game-servers-task-definition
  echo "Done creating stack!"
else
  echo "Updating fargate-game-servers-task-definition stack (this will take some time)..."
  aws cloudformation --region $region update-stack --stack-name fargate-game-servers-task-definition \
     --template-body file://game-server-task-definition.yaml \
     --parameters ParameterKey=Image,ParameterValue=$accountid.dkr.ecr.$region.amazonaws.com/fargate-game-servers:$build_id \
     --capabilities CAPABILITY_IAM
  aws cloudformation --region $region wait stack-update-complete --stack-name fargate-game-servers-task-definition
  echo "Done updating stack!"
fi