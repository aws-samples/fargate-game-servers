#!/bin/bash

# Get the configuration variables
source ../configuration.sh

# Returns the status of a stack
getstatusofstack() {
	aws cloudformation describe-stacks --region $region --stack-name $1 --query Stacks[].StackStatus --output text 2>/dev/null
}

# Deploy the Cognito Resources
stackstatus=$(getstatusofstack fargate-game-servers-cognito)
if [ -z "$stackstatus" ]; then
  echo "Creating Cognito Resources stack (this will take some time)..."
  aws cloudformation --region $region create-stack --stack-name fargate-game-servers-cognito \
      --template-body file://cognito.yaml \
      --capabilities CAPABILITY_IAM
  aws cloudformation --region $region wait stack-create-complete --stack-name fargate-game-servers-cognito
  echo "Done creating stack!"
else
  echo "Updating Cognito Resources stack (this will take some time)..."
  aws cloudformation --region $region update-stack --stack-name fargate-game-servers-cognito \
     --template-body file://cognito.yaml \
     --capabilities CAPABILITY_IAM
  aws cloudformation --region $region wait stack-update-complete --stack-name fargate-game-servers-cognito
  echo "Done updating stack!"
fi

echo "You need this Identity pool ID in MatchmakingClient.cs:"
echo $(aws cloudformation --region $region describe-stacks --stack-name fargate-game-servers-cognito --query "Stacks[0].Outputs[0].OutputValue")