﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.SharePoint;
using Microsoft.SharePoint.Administration;
using Test.Activities.Automation.ActivityLib;
using Test.Activities.Automation.TimerJob.Models;

namespace Test.Activities.Automation.TimerJob
{
    public class TimerJob : SPJobDefinition
    {
        private ILogger _logger;

        public TimerJob()
        {
            _logger = new ULSLogger(GetType().FullName);
        }

        public TimerJob(string name, SPWebApplication webApp, SPServer server, SPJobLockType lockType)
            : base(name, webApp, server, lockType)
        {
        }

        public override void Execute(Guid targetInstanceId)
        {
            _logger.LogInformation("Executing timer");

            try
            {
                var devActivities = GetDevActivities();

                var mentoringActivities = GetMentoringActivities();

                var allActivities = devActivities.Concat(mentoringActivities).ToList();

                SendActivities(allActivities);
            }
            catch (Exception e)
            {
                _logger.LogError(e.Message);
            }
            _logger.LogInformation("Timer executed");
        }

        private IEnumerable<ActivityInfo> GetDevActivities()
        {
            var repositories = GetConfigRepositories();

            GetRepoCommits(repositories);

            return CreateRepoActivities(repositories);
        }

        private IEnumerable<ActivityInfo> CreateRepoActivities(IEnumerable<Repository> repositories)
        {
            var activities = new List<ActivityInfo>();
            foreach (var repo in repositories)
                foreach (var branch in repo.Branches)
                    activities.AddRange(branch.Commits.Select(commit => new ActivityInfo
                    { Activity = repo.Activity, Date = DateTime.Parse(commit.Date), UserEmail = commit.AuthorEmail }));

            return activities.GroupBy(x => x.UserEmail).Select(x => x.First());
        }

        private IEnumerable<Repository> GetConfigRepositories()
        {
            IEnumerable<SPListItem> configItems;

            using (var site = new SPSite(Constants.Host))
            using (var web = site.OpenWeb(Constants.Web))
            {
                var configList = web.Lists.TryGetList(Constants.Lists.Configurations);
                configItems = configList?.GetItems()?.Cast<SPListItem>();
            }

            return configItems?.Where(item =>
                    item[Constants.Configuration.Key] as string ==
                    Constants.Configuration.ConfigurationKeys.GitLabRepository)
                .Select(item => item[Constants.Configuration.Value] as string)
                .Select(value =>
                    value?.Split(new[] { Constants.Configuration.Separator }, StringSplitOptions.RemoveEmptyEntries))
                .Select(str => new Repository { Host = str?[0], ProjectId = str?[1], Activity = str?[2] })
                .ToList();
        }

        private void GetRepoCommits(IEnumerable<Repository> repositories)
        {
            using (var handler = new HttpClientHandler())
            using (var client = new HttpClient(handler))
            {
                client.DefaultRequestHeaders.Add(Constants.GitLab.PrivateToken, Constants.GitLab.Token);
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue(Constants.HttpHeader.MediaType.ApplicationJson));

                foreach (var repo in repositories)
                {
                    var branches = GetBranches(client, repo);

                    foreach (var branch in branches)
                    {
                        var commits = GetBranchCommits(client, branch, repo);

                        branch.Commits = new List<Commit>(commits);
                    }

                    repo.Branches = new List<Branch>(branches);
                }
            }
        }

        private IEnumerable<Commit> GetBranchCommits(HttpClient client, Branch branch, Repository repo)
        {
            var url =
                $"{repo.Host}{Constants.GitLab.Api}/{repo.ProjectId}/{Constants.GitLab.Commits}" +
                $"?{Constants.GitLab.Branch}{branch.Name}" +
                $"&{Constants.GitLab.Since}{DateTime.Now.AddDays(-1).Date:yyyy-MM-dd}" +
                $"&{Constants.GitLab.Until}{DateTime.Now.Date:yyyy-MM-dd}";

            return GetApiCollection<Commit>(url, client);
        }

        private IEnumerable<Branch> GetBranches(HttpClient client, Repository repo)
        {
            var url =
                $"{repo.Host}{Constants.GitLab.Api}/{repo.ProjectId}/{Constants.GitLab.Branches}";

            return GetApiCollection<Branch>(url, client);
        }

        private IEnumerable<T> GetApiCollection<T>(string url, HttpClient client) where T : class
        {
            var response = client.GetAsync(url).Result;
            var stream = response.Content.ReadAsStreamAsync().Result;

            var res = new List<T>();

            var reader = new StreamReader(stream);
            stream.Position = 0;

            var ch = reader.Read();
            if (ch == '[')
            {
                var multiple = JsonDeserialize<T[]>(stream);
                res.AddRange(multiple);

                return res;
            }

            var single = JsonDeserialize<T>(stream);
            res.Add(single);

            return res;
        }

        private T JsonDeserialize<T>(Stream stream) where T : class
        {
            var serializer = new DataContractJsonSerializer(typeof(T));

            stream.Position = 0;
            var res = serializer.ReadObject(stream) as T;
            return res;
        }

        private IEnumerable<ActivityInfo> GetMentoringActivities()
        {
            var activities = new List<ActivityInfo>();
            SPList mentoringList;

            using (var site = new SPSite(Constants.Host))
            using (var web = site.OpenWeb(Constants.Web))
            {
                mentoringList = web.Lists.TryGetList(Constants.Lists.MentoringCalendar);
            }

            var eventsCollection = mentoringList?.GetItems();
                var yesterday = DateTime.Now.AddDays(-1).Date;
                var events = eventsCollection?.Cast<SPListItem>()
                    .Where(x => x[Constants.Calendar.StartTime] as DateTime? <= yesterday)
                    .Where(x => x[Constants.Calendar.EndTime] as DateTime? >= yesterday);

                if (events == null)
                {
                    return activities;
                }

                foreach (var item in events)
                {
                    var userField = item.Fields.GetField(Constants.Calendar.Employee);
                    var userFieldValue =
                        userField?.GetFieldValue(item[Constants.Calendar.Employee].ToString()) as SPFieldUserValue;
                    activities.Add(new ActivityInfo
                    {
                        UserEmail = userFieldValue?.User.Email,
                        Activity = Constants.Activity.ActivityType.Mentoring,
                        Date = yesterday
                    });
                }

            return activities.GroupBy(x => x.UserEmail).Select(x => x.First());
        }

        private async void SendActivities(IEnumerable<ActivityInfo> activities)
        {
            var serializer = new DataContractJsonSerializer(typeof(List<ActivityInfo>));

            var ms = new MemoryStream();
            serializer.WriteObject(ms, activities);

            ms.Position = 0;
            var reader = new StreamReader(ms);
            var str = reader.ReadToEnd();

            using (var svcHandler = new HttpClientHandler { UseDefaultCredentials = true })
            using (var svcClient = new HttpClient(svcHandler))
            {
                var content = new StringContent(str, Encoding.UTF8, Constants.HttpHeader.MediaType.ApplicationJson);
                var uri = new Uri(Constants.ServiceUrl);

                for (var i = 0; i < 3; i++)
                {
                    var res = await svcClient.PostAsync(uri, content);
                    if (res.StatusCode == HttpStatusCode.OK) break;

                    await Task.Delay(new TimeSpan(0, 5, 0));
                }
            }
        }
    }
}
