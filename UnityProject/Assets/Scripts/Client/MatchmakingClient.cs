﻿// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using UnityEngine;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using AWSSignatureV4_S3_Sample.Signers;

#if CLIENT

// **** MATCMAKING API CLIENT ***
// The Backend service called by this client will do simple placement of players to new or existing sessions

public class MatchmakingClient
{
    // **** SET THESE VARIABLES BASED ON YOUR OWN CONFIGURATION *** //
    static string apiEndpoint = "<YOUR-API-ENDPOINT>";
    public static string identityPoolID = "<YOUR-USERPOOL-ID>";
    public static string regionString = "us-east-1";
    public static Amazon.RegionEndpoint region = Amazon.RegionEndpoint.USEast1;
    // *********************************************************** //

    // Helper function to send and wait for response to a signed request to the API Gateway endpoint
    async Task<string> SendSignedGetRequest(string requestUrl)
    {
        // Sign the request with cognito credentials
        var request = this.generateSignedRequest(requestUrl);

        // Execute the signed request
        var client = new HttpClient();
        var resp = await client.SendAsync(request);

        // Get the response
        var responseStr = await resp.Content.ReadAsStringAsync();
        Debug.Log(responseStr);
        return responseStr;
    }

    // Request a game session from the backend
    public GameSessionInfo RequestGameSession()
    {
        try
        {
            //Make the signed request and wait for max 10 seconds to complete
            var response = Task.Run(() => this.SendSignedGetRequest(apiEndpoint + "requestgamesession"));
            response.Wait(10000);
            string jsonResponse = response.Result;
            Debug.Log("Json response: " + jsonResponse);

            if (jsonResponse.Contains("failed"))
                return null;

            GameSessionInfo info = JsonUtility.FromJson<GameSessionInfo>(jsonResponse);
            return info;
        }
        catch (Exception e)
        {
            Debug.Log(e.Message);
            return null;
        }
    }

    // Generates a HTTPS requestfor API Gateway signed with the Cognito credentials from a url using the S3 signer tool example
    // NOTE: You need to add the floders "Signers" and "Util" to the project from the S3 signer tool example: https://docs.aws.amazon.com/AmazonS3/latest/API/samples/AmazonS3SigV4_Samples_CSharp.zip
    HttpRequestMessage generateSignedRequest(string url)
    {
        var endpointUri = url;

        var uri = new Uri(endpointUri);

        var headers = new Dictionary<string, string>
            {
                {AWS4SignerBase.X_Amz_Content_SHA256, AWS4SignerBase.EMPTY_BODY_SHA256},
            };

        var signer = new AWS4SignerForAuthorizationHeader
        {
            EndpointUri = uri,
            HttpMethod = "GET",
            Service = "execute-api",
            Region = regionString
        };

        //Extract the query parameters
        var queryParams = "";
        if (url.Split('?').Length > 1)
        {
            queryParams = url.Split('?')[1];
        }

        var authorization = signer.ComputeSignature(headers,
                                                    queryParams,
                                                    AWS4SignerBase.EMPTY_BODY_SHA256,
                                                    Client.cognitoCredentials.AccessKey,
                                                    Client.cognitoCredentials.SecretKey);

        headers.Add("Authorization", authorization);

        var request = new HttpRequestMessage
        {
            Method = HttpMethod.Get,
            RequestUri = new Uri(url),
        };

        // Add the generated headers to the request
        foreach (var header in headers)
        {
            try
            {
                if (header.Key != null && header.Value != null)
                    request.Headers.Add(header.Key, header.Value);
            }
            catch (Exception e)
            {
                Debug.Log("error: " + e.GetType().ToString());
            }
        }

        // Add the IAM authentication token
        request.Headers.Add("x-amz-security-token", Client.cognitoCredentials.Token);

        return request;
    }
}

#endif