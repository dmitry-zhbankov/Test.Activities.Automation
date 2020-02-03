﻿using System.Runtime.Serialization;

namespace Test.Activities.Automation.TimerJob
{
    [DataContract]
    public class Commit
    {
        [DataMember(Name = "author_email")]
        public string AuthorEmail { get; set; }

        [DataMember(Name = "created_at")]
        public string Date { get; set; }
    }
}