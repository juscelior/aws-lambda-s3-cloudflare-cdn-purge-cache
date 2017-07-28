using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.Lambda.S3Events;
using System.Net.Http;
using System.Text;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace AWSLambdaS3.CloudFlarePurgeCache
{
    public class Function
    {
        IAmazonS3 S3Client { get; set; }
        IAmazonCloudFront cfClient { get; set; }
        string distribuitionCloudFrontId { get; set; }


        /// <summary>
        /// Default constructor. This constructor is used by Lambda to construct the instance. When invoked in a Lambda environment
        /// the AWS credentials will come from the IAM role associated with the function and the AWS region will be set to the
        /// region the Lambda function is executed in.
        /// </summary>
        public Function()
        {
            S3Client = new AmazonS3Client();

            distribuitionCloudFrontId = Environment.GetEnvironmentVariable("CloudFrontDistribution");
            if (!string.IsNullOrWhiteSpace(distribuitionCloudFrontId))
            {
                cfClient = new AmazonCloudFrontClient();
            }
        }

        /// <summary>
        /// Constructs an instance with a preconfigured S3 client. This can be used for testing the outside of the Lambda environment.
        /// </summary>
        /// <param name="s3Client"></param>
        public Function(IAmazonS3 s3Client, IAmazonCloudFront cfClient)
        {
            this.S3Client = s3Client;

            if (cfClient != null)
            {
                this.cfClient = cfClient;
            }
        }

        /// <summary>
        /// This method is called for every Lambda invocation. This method takes in an S3 event object and can be used 
        /// to respond to S3 notifications.
        /// </summary>
        /// <param name="evnt"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task<string> FunctionHandler(S3Event evnt, ILambdaContext context)
        {
            var s3Event = evnt.Records?[0].S3;
            if (s3Event == null)
            {
                return null;
            }

            try
            {

                Task<CreateInvalidationResponse> invalidationResponseTask = null;

                if (!string.IsNullOrWhiteSpace(distribuitionCloudFrontId))
                {
                    CreateInvalidationRequest invaliationReq = new CreateInvalidationRequest()
                    {
                        DistributionId = distribuitionCloudFrontId,
                        InvalidationBatch = new InvalidationBatch(DateTime.Now.Ticks.ToString())
                        {
                            Paths = new Paths()
                            {
                                Quantity = 1,
                                Items = new List<string>() {
                                    $"/{s3Event.Object.Key}"
                                }
                            }
                        }
                    };

                    invalidationResponseTask = this.cfClient.CreateInvalidationAsync(invaliationReq);
                }

                HttpResponseMessage message = null;
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("X-Auth-Email", Environment.GetEnvironmentVariable("Email"));
                    client.DefaultRequestHeaders.Add("X-Auth-Key", Environment.GetEnvironmentVariable("Key"));

                    StringBuilder msg = new StringBuilder();
                    msg.Append(@"{""files"":[""");
                    msg.Append(Environment.GetEnvironmentVariable("Domain"));
                    msg.Append("/");
                    msg.Append(s3Event.Object.Key);
                    msg.Append(@"""]}");

                    StringContent content = new StringContent(msg.ToString(), Encoding.UTF8, "application/json");

                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, new Uri(string.Format("https://api.cloudflare.com/client/v4/zones/{0}/purge_cache", Environment.GetEnvironmentVariable("Zone")))))
                    {
                        request.Content = content;
                        message = await client.SendAsync(request);
                    }
                }

                if (invalidationResponseTask != null)
                {
                    await invalidationResponseTask;
                }

                return await message.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                context.Logger.LogLine($"Error getting object {s3Event.Object.Key} from bucket {s3Event.Bucket.Name}. Make sure they exist and your bucket is in the same region as this function.");
                context.Logger.LogLine(e.Message);
                context.Logger.LogLine(e.StackTrace);
                throw;
            }
        }
    }
}
