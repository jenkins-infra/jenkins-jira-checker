#r "System.Xml.Linq"

using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

using Octokit;
using Atlassian.Jira;

public delegate Task<object> VerificationDelegate(Atlassian.Jira.Issue issue, HashSet<VerificationMessage> hostingIssues, TraceWriter log);

static string INVALID_FORK_FROM = "Repository URL '{0}' is not a valid GitHub repository (check that you do not have .git at the end, GitHub API doesn't support this).";
static string INVALID_POM = "The pom.xml file in the root of the origin repository is not valid";
static bool debug = false;

public class VerificationMessage : IEquatable<VerificationMessage> {
    public enum Severity {
        Info,
        Warning,
        Required,
    }

    public string Message { get; private set; }
    public Severity SeverityLevel { get; private set; }
    public IList<VerificationMessage> Subitems { get; private set; }

    public VerificationMessage(Severity severity, List<VerificationMessage> subitems, string format, params object[] args) {
        Message = string.Format(format, args);
        SeverityLevel = severity;
        Subitems = subitems;
    }

    public VerificationMessage(Severity severity, string format, params object[] args) : this(severity, null, format, args) {
        // we just call the other constructor
    }

    public bool Equals(VerificationMessage other) {
        if(SeverityLevel != other.SeverityLevel) {
            return false;
        }

        if(Message != other.Message) {
            return false;
        }

        return true;
    }    
}

public static string SeverityToFriendlyString(VerificationMessage.Severity severity) {
    switch(severity) {
        case VerificationMessage.Severity.Info:
            return "INFO";
        case VerificationMessage.Severity.Warning:
            return "WARNING";
    }
    return "REQUIRED";
}

public static string SeverityToColor(VerificationMessage.Severity severity) {
    switch(severity) {
        case VerificationMessage.Severity.Info:
            return "black";
        case VerificationMessage.Severity.Warning:
            return "orange";
    }
    return "red";
}

public static async Task<HttpResponseMessage> Run(HttpRequestMessage req, TraceWriter log)
{
    var verifications = new Dictionary<string, VerificationDelegate>() {
        { "JIRA Fields", VerifyJiraFields },
        { "GitHub", VerifyGitHubInfo },
        { "Maven", VerifyMaven },
        /*{ "Gradle", VerifyGradle }*/
    };

    var debugSetting = GetEnvironmentVariable("DEBUG");
    if(!string.IsNullOrWhiteSpace(debugSetting)) {
        debug = true;
        log.Info("Running in debug mode");
    }

    // Get request body
    dynamic data = await req.Content.ReadAsAsync<object>();

    string webhookEvent = data?.webhookEvent;

    if(string.IsNullOrEmpty(webhookEvent)) {
        return req.CreateResponse(HttpStatusCode.BadRequest, "Invalid payload for webhook");
    }

    if((webhookEvent == "jira:issue_updated" || webhookEvent == "jira:issue_created") && data?.issue != null) {
        var hostingIssues = new HashSet<VerificationMessage>();

        string key = data.issue.key;
        var jira = CreateJiraClient();
        var issue = await jira.Issues.GetIssueAsync(key);
        if(issue == null) {
            log.Error($"Could not retrieve JIRA issue {key}");
            return req.CreateResponse(HttpStatusCode.BadRequest, "Could not retrieve JIRA issue");
        }

        foreach(var verification in verifications) {
            log.Info($"Running verification '{verification.Key}'");
            try {
                await verification.Value(issue, hostingIssues, log);
            } catch(Exception ex) {
                log.Info($"Error running verification '{verification.Key}': {ex.ToString()}");
            }
        }

        var msg = new StringBuilder("Hello from your friendly Jenkins Hosting Checker\n\n");
        log.Info("Checking if there were errors");
        if(hostingIssues.Count > 0) {
            msg.AppendLine("It appears you have some issues with your hosting request. Please see the list below and "
                        + "correct all issues marked {color:red}REQUIRED{color}. Your hosting request will not be " 
                        + "approved until these issues are corrected. Issues marked with {color:orange}WARNING{color} "
                        + "or INFO are just recommendations and will not stall the hosting process.\n");
            log.Info("Appending issues to msg");
            AppendIssues(msg, hostingIssues, 1);
        } else {
            msg.Append("It looks like you have everything in order for your hosting request. "
                        + "A human volunteer will check over things that I am not able to check for "
                        + "(code review, README content, etc) and process the request as quickly as possible. "
                        + "Thank you for your patience.");
        }

        log.Info(msg.ToString());
        if(!debug) {
            await issue.AddCommentAsync(msg.ToString());
        }        
    } else {
        return req.CreateResponse(HttpStatusCode.BadRequest, "Invalid webhook type");
    }

    return req.CreateResponse(HttpStatusCode.OK);
}

public static void AppendIssues(StringBuilder msg, IEnumerable<VerificationMessage> issues, int level) {
    foreach(var issue in issues.OrderByDescending(x => x.SeverityLevel)) {
        if(level == 1) {
            msg.AppendLine(string.Format("{0} {{color:{1}}}{2}: {3}{{color}}", new String('*', level), SeverityToColor(issue.SeverityLevel), SeverityToFriendlyString(issue.SeverityLevel), issue.Message));
        } else {
            msg.AppendLine(string.Format("{0} {1}", new String('*', level), issue.Message));
        }
        if(issue.Subitems != null) {
            AppendIssues(msg, issue.Subitems, level+1);
        }
    }
}

public static string GetEnvironmentVariable(string name)
{
    return System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
}

public static GitHubClient CreateGitHubClient() {
    var ghClient = new GitHubClient(new ProductHeaderValue("jenkins-hosting-checker"));
    ghClient.Credentials = new Credentials(GetEnvironmentVariable("GITHUB_APP_KEY"));
    return ghClient;
}

public static Jira CreateJiraClient() {
    return Jira.CreateRestClient(GetEnvironmentVariable("JIRA_URL"), GetEnvironmentVariable("JIRA_USERNAME"), GetEnvironmentVariable("JIRA_PASSWORD"));
}

public static async Task<object> VerifyJiraFields(Atlassian.Jira.Issue issue, HashSet<VerificationMessage> hostingIssues, TraceWriter log) {
    var userList = issue["GitHub Users to Authorize as Committers"]?.Value;
    var forkFrom = issue["Repository URL"]?.Value;
    var forkTo = issue["New Repository Name"]?.Value;
    
    // check list of users
    if(string.IsNullOrWhiteSpace(userList)) {
        hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "Missing list of users to authorize in 'GitHub Users to Authorize as Committers'"));
    }

    if(string.IsNullOrWhiteSpace(forkFrom)) {
        hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_FORK_FROM, ""));
    } else {
        // check the repo they want to fork from to make sure it conforms
        var m = Regex.Match(forkFrom, @"(?:https:\/\/github\.com/)?(\S+)\/(\S+)", RegexOptions.IgnoreCase);
        if(!m.Success) {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_FORK_FROM, forkFrom));
        }
    }

    if(string.IsNullOrWhiteSpace(forkTo)) {
        var subitems = new List<VerificationMessage>() {
            new VerificationMessage(VerificationMessage.Severity.Required, "It must match the artifactId (with -plugin added) from your build file (pom.xml/build.gradle)."),
            new VerificationMessage(VerificationMessage.Severity.Required, "It must end in -plugin if hosting request is for a Jenkins plugin."),
            new VerificationMessage(VerificationMessage.Severity.Required, "It must be all lowercase."),
            new VerificationMessage(VerificationMessage.Severity.Required, "It must NOT contain \"Jenkins\"."),
            new VerificationMessage(VerificationMessage.Severity.Required, "It must use hyphens ( - ) instead of spaces.")
        };
        hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, subitems, "You must specify the repository name to fork to in 'New Repository Name' field with the following rules:"));
    } else {
        var forkToLower = forkTo.ToLower();
        if(forkToLower.Contains("jenkins") || forkToLower.Contains("hudson")) {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "'New Repository Name' must NOT include jenkins or hudson"));
        }

        if(!forkToLower.EndsWith("-plugin")) {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "'New Repository Name' must end with \"-plugin\" (disregard if you are not requesting hosting of a plugin)"));
        }

        if(forkToLower != forkTo) {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The 'New Repository Name' ({0}) must be all lowercase", forkTo));
        }
    }
    return null;
}

public static async Task<object> VerifyGitHubInfo(Atlassian.Jira.Issue issue, HashSet<VerificationMessage> hostingIssues, TraceWriter log) {
    var ghClient = CreateGitHubClient();
    var userList = issue["GitHub Users to Authorize as Committers"]?.Value;
    var forkFrom = issue["Repository URL"]?.Value;

    if(!string.IsNullOrWhiteSpace(userList)) {
        var users = userList.Split(new char[] { '\n', ';', ','}, StringSplitOptions.RemoveEmptyEntries);
        var invalidUsers = new List<string>();
        var orgs = new List<string>();
        foreach(var user in users) {
            try {
                var ghUser = await ghClient.User.Get(user.Trim());
                if(ghUser == null) {
                    invalidUsers.Add(user.Trim());
                }
            } catch(Exception) {
                try {
                    var ghOrg = await ghClient.Organization.Get(user.Trim());
                    if(ghOrg != null) {
                        orgs.Add(user.Trim());
                    }
                } catch {
                    invalidUsers.Add(user.Trim());
                }
            }
        }

        if(invalidUsers.Count > 0) {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The following usernames in 'GitHub Users to Authorize as Committers' are not valid GitHub usernames: {0}", string.Join(",", invalidUsers.ToArray())));
        }

        if(orgs.Count > 0) {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The following names in 'GitHub Users to Authorize as Committers' are organizations instead of users, this is not supported: {0}", string.Join(",", orgs.ToArray())));
        }
    }

    if(!string.IsNullOrWhiteSpace(forkFrom)) {
        var m = Regex.Match(forkFrom, @"(?:https:\/\/github\.com/)?(\S+)\/(\S+)", RegexOptions.IgnoreCase);
        if(m.Success) {
            string owner = m.Groups[1].Value;
            string repoName = m.Groups[2].Value;
            Repository repo = null;

            if(repoName.EndsWith(".git")) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The origin repositor '{0}' ends in .git, please remove this", forkFrom));
                repoName = repoName.Substring(0, repoName.Length - 4);
            }

            try {
                repo = await ghClient.Repository.Get(owner, repoName);
            } catch(Exception) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_FORK_FROM, forkFrom));
            }

            if(repo != null) {
                try {
                    var readme = ghClient.Repository.Content.GetReadme(owner, repoName);
                } catch(Octokit.NotFoundException) {
                    hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "Repository '{0}' does not contain a README.", forkFrom));
                }

                // check if the repo was originally forked from jenkinsci
                try {
                    Repository parent = repo.Parent;
                    if(parent != null && parent.FullName.StartsWith("jenkinsci")) {
                        hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "Repository '{0}' is currently showing as forked from a jenkinsci org repository, this relationship needs to be broken", forkFrom));
                    }
                } catch {

                }
            } else {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_FORK_FROM, forkFrom));
            }
        }
    }
    return null;
}

#region Maven Checks

public static async Task<object> VerifyMaven(Atlassian.Jira.Issue issue, HashSet<VerificationMessage> hostingIssues, TraceWriter log) {
    var ghClient = CreateGitHubClient();
    var forkFrom = issue["Repository URL"]?.Value;
    var forkTo = issue["New Repository Name"]?.Value;

    if(!string.IsNullOrEmpty(forkFrom)) {
        var m = Regex.Match(forkFrom, @"(?:https:\/\/github\.com/)?(\S+)\/(\S+)", RegexOptions.IgnoreCase);
        if(m.Success) {
            string owner = m.Groups[1].Value;
            string repoName = m.Groups[2].Value;

            Repository repo = await ghClient.Repository.Get(owner, repoName);
            try {
                var pomXml = await ghClient.Repository.Content.GetAllContents(owner, repoName, "pom.xml");
                if(pomXml.Count > 0) {
                    // the pom.xml file should be text, so we can just use .Content
                    try {
                        var doc = XDocument.Parse(pomXml[0].Content);
                        if(!string.IsNullOrWhiteSpace(forkTo)) {
                            CheckArtifactId(doc, forkTo, hostingIssues, log);
                        }
                        CheckParentInfo(doc, hostingIssues, log);
                        CheckName(doc, hostingIssues, log);
                        CheckLicenses(doc, hostingIssues, log);
                    } catch(Exception ex) {
                        log.Info(string.Format("Exception occured trying to look at pom.xml: {0}", ex.ToString()));
                        hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_POM));
                    }
                }
            } catch(Octokit.NotFoundException) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Warning, 
                    "No pom.xml found in root of project, if you are using a different build system, or this is not a plugin, you can disregard this message"));
            }
        } else {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_FORK_FROM, forkFrom));
        }
    }
    return null;
}

public static void CheckArtifactId(XDocument doc, string forkTo, HashSet<VerificationMessage> hostingIssues, TraceWriter log) {
    XNamespace ns = "http://maven.apache.org/POM/4.0.0";
    try {
        var artifactIdNode = doc.Element(ns + "project").Element(ns + "artifactId");
        if(artifactIdNode != null && artifactIdNode.Value != null) {
            var artifactId = artifactIdNode.Value;
            if(string.Compare(artifactId, forkTo.Replace("-plugin", string.Empty), true) != 0) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The <artifactId> from the pom.xml ({0}) is incorrect, it should be {1} (new repository name with -plugin removed)", artifactId, forkTo.Replace("-plugin", string.Empty)));
            }

            if(artifactId.ToLower().Contains("jenkins")) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The <artifactId> from the pom.xml ({0}) should not contain \"Jenkins\"", artifactId));
            }

            if(artifactId.ToLower() != artifactId) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The <artifactId> from the pom.xml ({0}) should be all lower case", artifactId));
            }
        } else {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The pom.xml file does not contain a valid <artifactId> for the project"));
        }
    } catch(Exception ex) {
        log.Info($"Error trying to access artifactId: {ex.ToString()}");
        hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_POM));
    }
}

public static void CheckName(XDocument doc, HashSet<VerificationMessage> hostingIssues, TraceWriter log) {
    XNamespace ns = "http://maven.apache.org/POM/4.0.0";
    try {
        var nameNode = doc.Element(ns + "project").Element(ns + "name");
        if(nameNode != null && nameNode.Value != null) {
            var name = nameNode.Value;
            if(string.IsNullOrWhiteSpace(name)) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The <name> from the pom.xml is blank or missing"));
            }

            if(name.ToLower().Contains("jenkins")) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The <name> should not contain \"Jenkins\""));
            }
        } else {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The pom.xml file does not contain a valid <name> for the project"));
        }
    } catch(Exception ex) {
        log.Info($"Error trying to access <name>: {ex.Message}");
        hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_POM));
    }
}

public static void CheckParentInfo(XDocument doc, HashSet<VerificationMessage> hostingIssues, TraceWriter log) {
    XNamespace ns = "http://maven.apache.org/POM/4.0.0";
    try {
        var parentNode = doc.Element(ns + "project").Element(ns + "parent");
        if(parentNode != null) {
            var groupIdNode = parentNode.Element(ns + "groupId");
            if(groupIdNode != null && groupIdNode.Value != null && groupIdNode.Value != "org.jenkins-ci.plugins") {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The groupId for your parent pom is not \"org.jenkins-ci.plugins\"."));
            }

            var versionNode = parentNode.Element(ns + "version");
            if(versionNode != null && versionNode.Value != null) {
                var jenkinsVersion = new Version(versionNode.Value);
                if(jenkinsVersion.Major == 2) {
                    versionNode = doc.Element(ns + "project").Element(ns + "properties").Element(ns + "jenkins.version");
                    if(versionNode != null && versionNode.Value != null) {
                        jenkinsVersion = new Version(versionNode.Value);
                    }

                    if(jenkinsVersion.Build <= 0) {
                        hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Info, "Your plugin does not seem to have a LTS Jenkins release. In general, "
                        + "it's preferable to use an LTS version as parent version."));
                    }
                } else {
                    hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, "The parent pom version '{0}' should be at least 2.11 or higher", jenkinsVersion));
                }
            }
        }
    } catch(Exception ex) {
        log.Info($"Error trying to access the <parent> information: {ex.Message}");
    }
}

public static void CheckLicenses(XDocument doc, HashSet<VerificationMessage> hostingIssues, TraceWriter log) {
    XNamespace ns = "http://maven.apache.org/POM/4.0.0";
    var SPECIFY_LICENSE = "Specify an open source license for your code (most plugins use MIT).";
    try {
        var licensesNode = doc.Element(ns + "project").Element(ns + "licenses");
        if(licensesNode != null) {
            if(licensesNode.Elements(ns + "license").Count() == 0) {
                hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, SPECIFY_LICENSE));
            }            
        } else {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, SPECIFY_LICENSE));
        }
    } catch(Exception ex) {
        log.Info($"Error trying to access the <licenses> information: {ex.Message}");
    }
}

#endregion

public static async Task<object> VerifyGradle(Atlassian.Jira.Issue issue, HashSet<VerificationMessage> hostingIssues, TraceWriter log) {
    var ghClient = CreateGitHubClient();
    var forkFrom = issue["Repository URL"]?.Value;
    var forkTo = issue["New Repository Name"]?.Value;

    if(!string.IsNullOrWhiteSpace(forkFrom)) {
        var m = Regex.Match(forkFrom, @"(?:https:\/\/github\.com/)?(\S+)\/(\S+)", RegexOptions.IgnoreCase);
        if(m.Success) {
            string owner = m.Groups[1].Value;
            string repoName = m.Groups[2].Value;

            Repository repo = await ghClient.Repository.Get(owner, repoName);
            try {
                var buildGradle = await ghClient.Repository.Content.GetAllContents(owner, repoName, "build.gradle");
                if(buildGradle != null && buildGradle.Count > 0) {
                    // look through looking for artifactId
                }
            } catch(Octokit.NotFoundException) {
                // we don't do anything here...this is much less common
            }
        } else {
            hostingIssues.Add(new VerificationMessage(VerificationMessage.Severity.Required, INVALID_FORK_FROM, forkFrom));
        }
    }
    return null;
}