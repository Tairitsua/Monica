using Monica.ApplicationModel.Annotations;
using Platform.Protocol.Application;
using Roles.Ordering.Reporting.Registration;

[assembly: ApplicationRole(ReferenceRoles.OrderingReporting, ReferenceDomains.Ordering, Registration = typeof(RoleRegistration))]
