# Jenkins JIRA Checker

The Jenkins JIRA Checker is used to check new hosting requests for validity, relieving some of the manual aspects of approving plugin hosting requests.

## Checks

The Jenkins JIRA Checker currently has the following checks implemented:
 * Basic JIRA field checks
   * Checks that all fields are filled in and have ok values
 * GitHub checks
   * Checks 'GitHub Users to Authorize as Committers' to see if they are valid GitHub users
   * Checks 'Repository URL' to verify it is a valid GitHub repo
 * Maven checks
   * Checks if there is a pom.xml and verifies various requirements
     * Checks <artifactId> vs. the 'New Repository Name' to verify they match
     * Checks <name> to verify it doesn't have bad values
     * Checks <parent> for recommended versions (LTS and minimum versions)
     * Checks that a license is specified
 * Gradle checks
   * NOT CURRENTLY IMPLEMENTED, JUST STUBBED 

## Configuration
 * To deploy the checker, you will need to setup the following Application settings:
   * JIRA_URL - the URL for your JIRA instance
   * JIRA_USERNAME - the username for the JIRA user for authentication
   * JIRA_PASSWORD - the password for the JIRA user for authentication
   * GITHUB_APP_KEY - the GitHub app key to use for GitHub API accesses
