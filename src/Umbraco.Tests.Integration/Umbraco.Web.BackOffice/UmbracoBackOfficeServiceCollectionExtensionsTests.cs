// Copyright (c) Umbraco.
// See LICENSE for more details.

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Core.Security;
using Umbraco.Extensions;
using Umbraco.Tests.Integration.Testing;

namespace Umbraco.Tests.Integration.Umbraco.Web.BackOffice
{
    [TestFixture]
    public class UmbracoBackOfficeServiceCollectionExtensionsTests : UmbracoIntegrationTest
    {
        protected override void CustomTestSetup(IUmbracoBuilder builder) => builder.Services.AddUmbracoBackOfficeIdentity();

        [Test]
        public void AddUmbracoBackOfficeIdentity_ExpectBackOfficeUserStoreResolvable()
        {
            IUserStore<BackOfficeIdentityUser> userStore = Services.GetService<IUserStore<BackOfficeIdentityUser>>();

            Assert.IsNotNull(userStore);
            Assert.AreEqual(typeof(BackOfficeUserStore), userStore.GetType());
        }

        [Test]
        public void AddUmbracoBackOfficeIdentity_ExpectBackOfficeClaimsPrincipalFactoryResolvable()
        {
            IUserClaimsPrincipalFactory<BackOfficeIdentityUser> principalFactory = Services.GetService<IUserClaimsPrincipalFactory<BackOfficeIdentityUser>>();

            Assert.IsNotNull(principalFactory);
            Assert.AreEqual(typeof(BackOfficeClaimsPrincipalFactory), principalFactory.GetType());
        }

        [Test]
        public void AddUmbracoBackOfficeIdentity_ExpectBackOfficeUserManagerResolvable()
        {
            IBackOfficeUserManager userManager = Services.GetService<IBackOfficeUserManager>();

            Assert.NotNull(userManager);
        }
    }
}
