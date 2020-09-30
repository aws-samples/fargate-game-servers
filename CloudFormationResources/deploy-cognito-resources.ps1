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

# Deploy the Cognito Resources
$stackstatus = Get-Status-Of-Stack fargate-game-servers-cognito

if ($stackstatus -eq $null) {
    Write-Host "Creating Cognito Resources stack (this will take some time)..."
    aws cloudformation --region $Config.Settings.AccountSettings.Region create-stack --stack-name fargate-game-servers-cognito `
      --template-body file://cognito.yaml `
      --capabilities CAPABILITY_IAM
    aws cloudformation --region $Config.Settings.AccountSettings.Region wait stack-create-complete --stack-name fargate-game-servers-cognito
    Write-Host "Done creating stack!"
    } else {
    Write-Host "Updating Cognito Resources stack (this will take some time)..."
    aws cloudformation --region $Config.Settings.AccountSettings.Region update-stack --stack-name fargate-game-servers-cognito `
     --template-body file://cognito.yaml `
     --capabilities CAPABILITY_IAM
    aws cloudformation --region $Config.Settings.AccountSettings.Region wait stack-update-complete --stack-name fargate-game-servers-cognito
    Write-Host "Done updating stack!"
    }

echo "You need this Identity pool ID in MatchmakingClient.cs:"
echo $(aws cloudformation --region $Config.Settings.AccountSettings.Region describe-stacks --stack-name fargate-game-servers-cognito --query "Stacks[0].Outputs[0].OutputValue")