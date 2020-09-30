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

# 1. Create ECR repository if it doesn't exits
aws ecr create-repository --repository-name fargate-game-servers --region $Config.Settings.AccountSettings.Region

# 2. Login to ECR (AWS CLI V2)
aws ecr get-login-password --region $Config.Settings.AccountSettings.Region | docker login --username AWS --password-stdin "$($Config.Settings.AccountSettings.AccountId).dkr.ecr.$($Config.Settings.AccountSettings.Region).amazonaws.com/fargate-game-servers"
#eval $(aws ecr get-login --region $Config.Settings.AccountSettings.Region --no-include-email) #This if for CLI V1

# 3. Create Docker Image from latest build (expected to be already created from Unity)
$build_id = Get-Date -UFormat "%y-%m-%d.%H%M%S"
docker build ../LinuxServerBuild/ -t "$($Config.Settings.AccountSettings.AccountId).dkr.ecr.$($Config.Settings.AccountSettings.Region).amazonaws.com/fargate-game-servers:$($build_id)"

# 4. Push the image to ECR
docker push "$($Config.Settings.AccountSettings.AccountId).dkr.ecr.$($Config.Settings.AccountSettings.Region).amazonaws.com/fargate-game-servers:$($build_id)"

# 5. Deploy an updated task definition with the new image
$stackstatus = Get-Status-Of-Stack fargate-game-servers-task-definition

if ($stackstatus -eq $null) {
    Write-Host "Creating fargate-game-servers-task-definition stack (this will take some time)..."
    aws cloudformation --region $Config.Settings.AccountSettings.Region create-stack --stack-name fargate-game-servers-task-definition `
      --template-body file://game-server-task-definition.yaml `
      --parameters ParameterKey=Image,ParameterValue="$($Config.Settings.AccountSettings.AccountId).dkr.ecr.$($Config.Settings.AccountSettings.Region).amazonaws.com/fargate-game-servers:$($build_id)" `
      --capabilities CAPABILITY_IAM
    aws cloudformation --region $Config.Settings.AccountSettings.Region wait stack-create-complete --stack-name fargate-game-servers-task-definition
    Write-Host "Done creating stack!"
    } else {
    Write-Host "pdating fargate-game-servers-task-definition stack (this will take some time)..."
    aws cloudformation --region $Config.Settings.AccountSettings.Region update-stack --stack-name fargate-game-servers-task-definition `
     --template-body file://game-server-task-definition.yaml `
     --parameters ParameterKey=Image,ParameterValue="$($Config.Settings.AccountSettings.AccountId).dkr.ecr.$($Config.Settings.AccountSettings.Region).amazonaws.com/fargate-game-servers:$($build_id)" `
     --capabilities CAPABILITY_IAM
    aws cloudformation --region $Config.Settings.AccountSettings.Region wait stack-update-complete --stack-name fargate-game-servers-task-definition
    Write-Host "Done updating stack!"
    }