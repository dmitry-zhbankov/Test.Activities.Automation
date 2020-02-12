﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SharePoint;
using Test.Activities.Automation.ActivityLib.Models.Helpers;
using Test.Activities.Automation.ActivityLib.Utils.Constants;

namespace Test.Activities.Automation.ActivityLib.Models.Activity.Services.SyncActivityService
{
    public partial class SyncActivityService
    {
        private ILogger _logger;

        public SyncActivityService(ILogger logger)
        {
            _logger = logger;
        }

        public void Sync(IEnumerable<ActivityInfo> activities)
        {
            _logger?.LogInformation("Synchronizing activities");

            using (var site = new SPSite(Constants.Host))
            using (var web = site.OpenWeb(Constants.Web))
            {
                var spActivities = GetSpActivities(web);

                var members = GetSpMembers(web);

                var compareDict = Ensure(spActivities, activities, members);

                web.AllowUnsafeUpdates = true;

                UpdateSpActivities(compareDict, web);
            }
        }

        private IEnumerable<Member> GetSpMembers(SPWeb web)
        {
            try
            {
                _logger?.LogInformation("Getting existing members from SP");

                var spMentorsList = web.Lists.TryGetList(Constants.Lists.Mentors);
                if (spMentorsList == null) throw new Exception("Getting SP mentor list failed");

                var spRootMentorsList = web.Lists.TryGetList(Constants.Lists.RootMentors);
                if (spRootMentorsList == null) throw new Exception("Getting SP root mentor list failed");

                var members = new List<Member>();

                foreach (var item in spMentorsList.GetItems().Cast<SPListItem>())
                {
                    var mentor = SPHelper.GetUserValue(item, Constants.Activity.Employee);

                    var paths = SPHelper.GetMultiChoiceValue(item, Constants.Activity.Paths);

                    members.Add(new Member
                    {
                        Mentor = mentor,
                        Paths = new List<string>(paths)
                    });
                }

                foreach (var item in spRootMentorsList.GetItems().Cast<SPListItem>())
                {
                    var rootMentor = SPHelper.GetUserValue(item, Constants.Activity.Employee);

                    var paths = SPHelper.GetMultiChoiceValue(item, Constants.Activity.Paths);

                    var member = members.FirstOrDefault(x => x.Mentor.ID == rootMentor.ID);

                    if (member != null)
                    {
                        member.RootMentor = rootMentor;
                    }
                    else
                    {
                        members.Add(new Member
                        {
                            RootMentor = rootMentor,
                            Paths = new List<string>(paths)
                        });
                    }
                }

                return members;
            }
            catch (Exception e)
            {
                throw new Exception($"Getting existing members from SP failed. {e.Message}");
            }
        }

        private Dictionary<ActivityKey, ActivityValue> Ensure(IEnumerable<SpActivity> spActivities, IEnumerable<ActivityInfo> activities, IEnumerable<Member> members)
        {
            var dict = InitDictionary(spActivities);

            foreach (var activity in activities)
            {
                UpdateDictionary(dict, activity, members);
            }

            return dict;
        }

        bool CheckActivityUser(ActivityInfo activity, IEnumerable<Member> members)
        {
            if (activity.UserId != null)
            {
                return members.Any(x => x.Mentor.ID == activity.UserId || x.RootMentor.ID == activity.UserId);
            }

            if (activity.UserEmail == null) return false;

            var id = GetUserIdByEmail(activity.UserEmail, members);
            if (id == null)
            {
                return false;
            }

            activity.UserId = id;
            return true;
        }

        private int? GetUserIdByEmail(string email, IEnumerable<Member> members)
        {
            var user = members.FirstOrDefault(x =>
                x.Mentor.Email == email || x.RootMentor.Email == email);
            
            if (user == null) return null;

            var id = user.Mentor?.ID ?? user.RootMentor.ID;
            return id;
        }

        private void UpdateDictionary(Dictionary<ActivityKey, ActivityValue> dict, ActivityInfo activity, IEnumerable<Member> members)
        {
            if (!CheckActivityUser(activity, members))
            {
                return;
            }

            var key = new ActivityKey()
            {
                UserId = (int)activity.UserId,
                Year = activity.Date.Year,
                Month = activity.Date.Month
            };

            if (dict.ContainsKey(key))
            {
                var value = dict[key];
                if (!value.Activities.Contains(activity.Activity))
                {
                    value.Activities.Add(activity.Activity);
                    dict[key].IsModified = true;
                }

                var set = new HashSet<string>(activity.Paths);
                set.ExceptWith(value.Paths);

                if (set.Count > 0)
                {
                    foreach (var item in set)
                    {
                        value.Paths.Add(item);
                    }
                    dict[key].IsModified = true;
                }
            }
            else
            {
                var newMember = members.FirstOrDefault(x =>
                    x.Mentor?.ID == activity.UserId || x.RootMentor?.ID == activity.UserId);

                if (newMember != null)
                {
                    var newKey = new ActivityKey()
                    {
                        UserId = (int)activity.UserId,
                        Year = activity.Date.Year,
                        Month = activity.Date.Month
                    };
                    var newValue = new ActivityValue()
                    {
                        Activities = new HashSet<string>()
                            {
                                activity.Activity
                            },
                        Paths = new HashSet<string>(activity.Paths),
                        Mentor = newMember.Mentor,
                        RootMentor = newMember.RootMentor,
                        IsModified = true
                    };

                    dict.Add(newKey, newValue);
                }
            }
        }

        Dictionary<ActivityKey, ActivityValue> InitDictionary(IEnumerable<SpActivity> spActivities)
        {
            var dict = new Dictionary<ActivityKey, ActivityValue>();

            foreach (var spActivity in spActivities)
            {
                int userId = 0;

                if (spActivity.RootMentor != null)
                {
                    userId = spActivity.RootMentor.ID;
                }
                else if (spActivity.Mentor != null)
                {
                    userId = spActivity.Mentor.ID;
                }

                var key = new ActivityKey()
                {
                    UserId = userId,
                    Year = spActivity.Year,
                    Month = spActivity.Month
                };

                var value = new ActivityValue()
                {
                    ActivityId = spActivity.Id,
                    Activities = new HashSet<string>(spActivity.Activities),
                    Paths = new HashSet<string>(spActivity.Paths),
                    Mentor = spActivity.Mentor,
                    RootMentor = spActivity.RootMentor
                };

                dict.Add(key, value);
            }

            return dict;
        }

        public IEnumerable<SpActivity> GetSpActivities(SPWeb web)
        {
            try
            {
                _logger?.LogInformation("Getting existing activities from SP");

                var spListActivities = web.Lists.TryGetList(Constants.Lists.Activities);
                if (spListActivities == null) throw new Exception("Getting SP activity list failed");

                var spListMentors = web.Lists.TryGetList(Constants.Lists.Mentors);
                if (spListMentors == null) throw new Exception("Getting SP mentors list failed");

                var spListRootMentors = web.Lists.TryGetList(Constants.Lists.RootMentors);
                if (spListRootMentors == null) throw new Exception("Getting SP root mentor list failed");

                var spActivities = new List<SpActivity>();

                foreach (var item in spListActivities.GetItems().Cast<SPListItem>())
                {
                    var mentor = SPHelper.GetLookUpUserValue(spListMentors, item, Constants.Activity.Mentor, Constants.Activity.Employee);
                    var rootMentor = SPHelper.GetLookUpUserValue(spListRootMentors, item, Constants.Activity.RootMentor, Constants.Activity.Employee);

                    var month = SPHelper.GetIntValue(item, Constants.Activity.Month);
                    var year = SPHelper.GetIntValue(item, Constants.Activity.Year);

                    var activities = SPHelper.GetMultiChoiceValue(item, Constants.Activity.Activities);
                    var paths = SPHelper.GetMultiChoiceValue(item, Constants.Activity.Paths);

                    spActivities.Add(new SpActivity
                    {
                        RootMentor = rootMentor,
                        Mentor = mentor,
                        Month = month,
                        Year = year,
                        Activities = new List<string>(activities),
                        Paths = new List<string>(paths),
                        Id = item.ID,
                    });
                }

                return spActivities;
            }
            catch (Exception e)
            {
                throw new Exception($"Getting existing activities from SP failed. {e.Message}");
            }
        }

        void UpdateSpActivities(Dictionary<ActivityKey, ActivityValue> dict, SPWeb web)
        {
            try
            {
                _logger?.LogInformation("Updating SP activities list");

                var spList = web.Lists.TryGetList(Constants.Lists.Activities);
                if (spList == null) throw new Exception("Getting SP list failed");

                var itemsToUpdate = dict.Where(x => x.Value.IsModified)
                    .Select(x => new SpActivity()
                    {
                        Id = x.Value.ActivityId,
                        Activities = new List<string>(x.Value.Activities),
                        Year = x.Key.Year,
                        Month = x.Key.Month,
                        Mentor = x.Value.Mentor,
                        RootMentor = x.Value.RootMentor,
                        Paths = new List<string>(x.Value.Paths)
                    });

                foreach (var item in itemsToUpdate)
                {
                    if (item.IsNew)
                    {
                        InsertActivity(spList, item);
                    }
                    else
                    {
                        UpdateActivity(spList, item);
                    }
                }
            }
            catch (Exception e)
            {
                throw new Exception($"Updating SP activities list failed. {e.Message}");
            }
        }

        private void InsertActivity(SPList spList, SpActivity item)
        {
            var newItem = spList.Items.Add();

            newItem[Constants.Activity.Mentor] = item.Mentor;
            newItem[Constants.Activity.RootMentor] = item.RootMentor;
            newItem[Constants.Activity.Month] = item.Month;
            newItem[Constants.Activity.Year] = item.Year;

            SPHelper.SetMultiChoiceValue(newItem, Constants.Activity.Activities, item.Activities);
            SPHelper.SetMultiChoiceValue(newItem, Constants.Activity.Paths, item.Paths);

            newItem.Update();
        }

        private void UpdateActivity(SPList spList, SpActivity item)
        {
            var itemToUpdate = spList.Items.GetItemById(item.Id);

            SPHelper.SetMultiChoiceValue(itemToUpdate, Constants.Activity.Activities, item.Activities);
            SPHelper.SetMultiChoiceValue(itemToUpdate, Constants.Activity.Paths, item.Paths);

            itemToUpdate.Update();

            //var activityField = itemToUpdate.Fields.GetField(Constants.Activity.Activities);
            //var activityFieldValue =
            //    activityField.GetFieldValue(itemToUpdate[Constants.Activity.Activities].ToString()) as
            //        SPFieldMultiChoiceValue;

            //for (var i = 0; i < activityFieldValue.Count; i++) item.Activities.Remove(activityFieldValue[i]);

            //foreach (var newItemActivity in item.Activities) activityFieldValue.Add(newItemActivity);

            //activityField.Update();
        }
    }
}
