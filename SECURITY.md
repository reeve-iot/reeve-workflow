# Security policy

## Supported versions

Only the latest published minor version receives fixes. Every npm and NuGet package in this repository carries the
same version number.

## Reporting a vulnerability

Report privately through GitHub private vulnerability reporting: open the repository's Security tab and choose
"Report a vulnerability". Do not open a public issue, and do not include a working exploit in the first message.
Expect an acknowledgement within a week and an assessment within two.

## What to keep in mind when hosting

The npm packages hold, validate and draw a graph; only `@reeve-iot/workflow-client` sends HTTP requests, to the server
you point it at. The NuGet packages compile and run graphs inside your process and store them in your PostgreSQL
database. Things a host is responsible for:

- Graph definitions are data. The stored-document reader refuses malformed input without throwing, but very large
  node counts or deep nesting still cost the host that loads them, so bound request bodies before they reach the
  endpoints.
- Build the port-type registry and the block catalogue from values you control. A registry cannot shadow a name, but
  it can widen assignability.
- A block the host did not write never receives the raw service container. A privileged block requires the capability
  of the workflow's owner, not of whoever triggered the run.
- Secret-capable inputs are stored through the host's codec and masked on read, and refusal messages never contain a
  value. Whoever can read the database and its key material can reveal them.
- The standard HTTP request block sends requests to the URL a workflow names, through the `HttpClient` you configure.
  Restricting target hosts is up to the host.
- Workflows are private to their owner, and the API answers "not found" for another owner's workflow. Every mapped
  API group must state its authorization (`RequireAuthorization` or `AllowAnonymous`), or the host does not start.
  The Editor also refuses writes from a principal with no subject claim.
- Display names and labels reach the DOM through React, which escapes them.
