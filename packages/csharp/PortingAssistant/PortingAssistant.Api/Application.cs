﻿using Amazon;
using ElectronCgi.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortingAssistant.Client.Model;
using PortingAssistant.Common.Model;
using PortingAssistant.Common.S3Upload;
using PortingAssistant.Common.Services;
using PortingAssistant.Common.Utils;
using PortingAssistant.VisualStudio;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using PortingAssistant.Client.NuGet.Interfaces;
using PortingAssistant.Telemetry.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;

namespace PortingAssistant.Api
{
    public class Application
    {
        private IServiceProvider _services { get; set; }
        private Connection _connection;
        private ILogger _logger;

        public Application(IServiceCollection serviceCollection, PortingAssistantSink portingAssistantSink)
        {
            _services = serviceCollection.BuildServiceProvider();
            _logger = _services.GetRequiredService<ILogger<Application>>();
            _connection = BuildConnection();
            portingAssistantSink.registerOnData((response) =>
            {
                _connection.Send("onDataUpdate", response);
            });


        }

        private Connection BuildConnection()
        {
            var serialiser = new PortingAssistantJsonSerializer();
            var channelMessageFactory = new ChannelMessageFactory(serialiser);
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return new Connection(
                    new Channel(new TabSeparatedInputStreamParser(), serialiser),
                    new MessageDispatcher(),
                    new RequestExecutor(serialiser, channelMessageFactory),
                    new ResponseHandlerExecutor(serialiser),
                    new System.Threading.Tasks.Dataflow.BufferBlock<IChannelMessage>(),
                    channelMessageFactory)
            {
                LogFilePath = "electron-cgi.log",
                MinimumLogLevel = LogLevel.Warning,
                IsLoggingEnabled = false
            };
        }

        public void SetupConnection(bool console = false)
        {
            _connection.On<AnalyzeSolutionRequest, Response<SolutionDetails, string>>("analyzeSolution", request =>
            {
                var assessmentService = _services.GetRequiredService<IAssessmentService>();

                assessmentService.AddApiAnalysisListener((response) =>
                {
                    _connection.Send("onApiAnalysisUpdate", response);
                });

                assessmentService.AddNugetPackageListener((response) =>
                {
                    _connection.Send("onNugetPackageUpdate", response);
                });

                return assessmentService.AnalyzeSolution(request);
            });

            _connection.On<ProjectFilePortingRequest, Response<List<PortingResult>, List<PortingResult>>>("applyPortingProjectFileChanges", request =>
            {
                var portingService = _services.GetRequiredService<IPortingService>();

                return portingService.ApplyPortingChanges(request);
            });

            _connection.On<string, Response<bool, string>>("openSolutionInIDE", request =>
            {
                try
                {
                    var portingService = _services.GetRequiredService<IPortingService>();
                    var vsfinder = _services.GetRequiredService<IVisualStudioFinder>();
                    var vsPath = vsfinder.GetLatestVisualStudioPath();
                    var vsexe = PortingAssistantUtils.FindFiles(vsPath, "devenv.exe");

                    if (vsexe == null)
                    {
                        return new Response<bool, string>
                        {
                            Status = Response<bool, string>.Failed(new Exception("No Visual Studio")),
                            ErrorValue = "A valid installation of Visual Studio was not found"
                        };
                    }

                    Process.Start(vsexe, request);
                    return new Response<bool, string>
                    {
                        Status = Response<bool, string>.Success()
                    };
                }
                catch (Exception ex)
                {
                    return new Response<bool, string>
                    {
                        Status = Response<bool, string>.Failed(ex),
                        ErrorValue = ex.Message
                    };
                }
            });

            _connection.On<string, bool>("checkInternetAccess", request =>
            {
                var httpService = _services.GetRequiredService<IHttpService>();
                try
                {
                    var file1 = httpService.DownloadS3FileAsync("newtonsoft.json.json.gz");
                    var file2 = httpService.DownloadS3FileAsync("52projects.json.gz");
                    var file3 = httpService.DownloadS3FileAsync("2a486f72.mega.json.gz");
                    Task.WhenAll(file1, file2, file3).Wait();
                    return file1.IsCompletedSuccessfully || file2.IsCompletedSuccessfully || file3.IsCompletedSuccessfully;
                }
                catch
                {
                    return false;
                }
            });

            _connection.On<CustomerFeedbackRequest, bool>("sendCustomerFeedback", request =>
            {
                // https://mqznodeyd1.execute-api.us-west-2.amazonaws.com/prod/s3?key=testmachine2/testtime2/metadata
                const string apiEndpoint = "https://mqznodeyd1.execute-api.us-west-2.amazonaws.com/prod/s3";
                string uniqueMachineID = LogUploadUtils.getUniqueIdentifier();
                string key = $"{uniqueMachineID}/{request.timestamp}/metadata";
                string requestUri = $"{apiEndpoint}?key={key}";
                var contentObj = new Content
                {
                    feedback = request.feedback,
                    category = request.category,
                    date = request.date,
                    email = request.email,
                    machineID = uniqueMachineID
                };
                string serializedContent = JsonConvert.SerializeObject(contentObj); 
                var content = new StringContent(serializedContent, Encoding.UTF8, "application/json");

                using var httpClient = new HttpClient();
                var response = httpClient.PutAsync(requestUri, content).Result;
                return response.IsSuccessStatusCode;
            });

        }

        public void Start()
        {
            try
            {
                _connection.Listen();

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection ended");
            }
        }
    }
}
