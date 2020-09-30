# Get the configuration variables
if (-not (Test-Path -Path "$PsScriptRoot\..\configuration.xml")) {
    throw 'The file does not exist'
} else {
    Write-Host 'Loading configuration file'
    [xml]$Config = Get-Content "$PsScriptRoot\..\configuration.xml"
}

# Create deployment bucket if it doesn't exist
if ($Config.Settings.AccountSettings.Region -eq "us-east-1") {
    aws s3api create-bucket --bucket $Config.Settings.S3Settings.DeploymentBucketName --region $Config.Settings.AccountSettings.Region
    } else {
    aws s3api create-bucket --bucket $Config.Settings.S3Settings.DeploymentBucketName --region $Config.Settings.AccountSettings.Region --create-bucket-configuration LocationConstraint="$($Config.Settings.AccountSettings.Region)"
    }

# Build, package and deploy the backend
sam build
sam package --s3-bucket $Config.Settings.S3Settings.DeploymentBucketName --output-template-file template_output.yaml
sam deploy --template-file template_output.yaml --region $Config.Settings.AccountSettings.Region --capabilities CAPABILITY_IAM --stack-name fargate-game-servers-backend