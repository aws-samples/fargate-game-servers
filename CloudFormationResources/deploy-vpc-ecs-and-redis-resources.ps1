# Get the configuration variables
if (-not (Test-Path -Path "$PsScriptRoot\..\configuration.xml")) {
    throw 'The file does not exist'
} else {
    Write-Host 'Loading configuration file'
    [xml]$Config = Get-Content "$PsScriptRoot\..\configuration.xml"
}

# Returns the status of a stack
Function Get-Status-Of-Stack{
  param ($Name)
	aws cloudformation describe-stacks --region $Config.Settings.AccountSettings.Region --stack-name $Name --query Stacks[].StackStatus --output text 2> Out-Null
}

$stackstatus = Get-Status-Of-Stack fargate-game-servers-ecs-resources

# Deploy the VPC and ECS resources with CloudFromation
if ($stackstatus -eq $null) {
    Write-Host "Creating ecs-resources stack (this will take some time)..."
    aws cloudformation --region $Config.Settings.AccountSettings.Region create-stack --stack-name fargate-game-servers-ecs-resources `
      --template-body file://ecs-resources.yaml `
      --capabilities CAPABILITY_IAM
    aws cloudformation --region $Config.Settings.AccountSettings.Region wait stack-create-complete --stack-name fargate-game-servers-ecs-resources
    Write-Host "Done creating stack!"
    } else {
    Write-Host "Updating ecs-resources stack (this will take some time)..."
    aws cloudformation --region $Config.Settings.AccountSettings.Region update-stack --stack-name fargate-game-servers-ecs-resources `
     --template-body file://ecs-resources.yaml `
     --capabilities CAPABILITY_IAM
    aws cloudformation --region $Config.Settings.AccountSettings.Region wait stack-update-complete --stack-name fargate-game-servers-ecs-resources
    Write-Host "Done updating stack!"
    }

$stackstatus = Get-Status-Of-Stack fargate-game-servers-elasticache-redis

# Deploy the Redis resources with CloudFromation
if ($stackstatus -eq $null) {
    Write-Host "Creating elasticache stack (this will take some time)..."
    aws cloudformation --region $Config.Settings.AccountSettings.Region create-stack --stack-name fargate-game-servers-elasticache-redis `
      --template-body file://elasticache-redis.yaml `
      --capabilities CAPABILITY_IAM
    aws cloudformation --region $Config.Settings.AccountSettings.Region wait stack-create-complete --stack-name fargate-game-servers-elasticache-redis
    Write-Host "Done creating stack!"
    } else {
    Write-Host "Updating elasticache stack (this will take some time)..."
    aws cloudformation --region $Config.Settings.AccountSettings.Region update-stack --stack-name fargate-game-servers-elasticache-redis `
     --template-body file://elasticache-redis.yaml `
     --capabilities CAPABILITY_IAM
    aws cloudformation --region $Config.Settings.AccountSettings.Region wait stack-update-complete --stack-name fargate-game-servers-elasticache-redis
    Write-Host "Done updating stack!"
    }