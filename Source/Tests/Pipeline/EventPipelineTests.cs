﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.Api.Tests.Utility;
using Exceptionless.Core.Billing;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Pipeline;
using Exceptionless.Core.Plugins.EventParser;
using Exceptionless.Core.Plugins.EventProcessor;
using Exceptionless.Core.Repositories;
using Exceptionless.Core.Models;
using Exceptionless.Core.Models.Data;
using Exceptionless.Tests.Utility;
using Nest;
using Xunit;

namespace Exceptionless.Api.Tests.Pipeline {
    public class EventPipelineTests : IDisposable {
        private readonly IOrganizationRepository _organizationRepository = IoC.GetInstance<IOrganizationRepository>();
        private readonly IProjectRepository _projectRepository = IoC.GetInstance<IProjectRepository>();
        private readonly ITokenRepository _tokenRepository = IoC.GetInstance<ITokenRepository>();
        private readonly IStackRepository _stackRepository = IoC.GetInstance<IStackRepository>();
        private readonly IEventRepository _eventRepository = IoC.GetInstance<IEventRepository>();
        private readonly UserRepository _userRepository = IoC.GetInstance<UserRepository>();
        
        [Fact]
        public async Task NoFutureEventsAsync() {
            await ResetAsync();

            var localTime = DateTime.Now;
            PersistentEvent ev = EventData.GenerateEvent(projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, occurrenceDate: localTime.AddMinutes(10));

            var pipeline = IoC.GetInstance<EventPipeline>();
            await pipeline.RunAsync(ev);

            var client = IoC.GetInstance<IElasticClient>();
            await client.RefreshAsync();
            ev = await _eventRepository.GetByIdAsync(ev.Id);
            Assert.NotNull(ev);
            Assert.True(ev.Date < localTime.AddMinutes(10));
            Assert.True(ev.Date - localTime < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task CanIndexExtendedDataAsync() {
            await ResetAsync();

            PersistentEvent ev = EventData.GenerateEvent(projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, generateData: false, occurrenceDate: DateTime.Now);
            ev.Data.Add("First Name", "Eric");
            ev.Data.Add("IsVerified", true);
            ev.Data.Add("IsVerified1", true.ToString());
            ev.Data.Add("Age", Int32.MaxValue);
            ev.Data.Add("Age1", Int32.MaxValue.ToString(CultureInfo.InvariantCulture));
            ev.Data.Add("AgeDec", Decimal.MaxValue);
            ev.Data.Add("AgeDec1", Decimal.MaxValue.ToString(CultureInfo.InvariantCulture));
            ev.Data.Add("AgeDbl", Double.MaxValue);
            ev.Data.Add("AgeDbl1", Double.MaxValue.ToString("r", CultureInfo.InvariantCulture));
            ev.Data.Add(" Birthday ", DateTime.MinValue);
            ev.Data.Add("BirthdayWithOffset", DateTimeOffset.MinValue);
            ev.Data.Add("@excluded", DateTime.MinValue);
            ev.Data.Add("Address", new { State = "Texas" });

            var pipeline = IoC.GetInstance<EventPipeline>();
            await pipeline.RunAsync(ev);
            Assert.Equal(11, ev.Idx.Count);
            Assert.True(ev.Idx.ContainsKey("first-name-s"));
            Assert.True(ev.Idx.ContainsKey("isverified-b"));
            Assert.True(ev.Idx.ContainsKey("isverified1-b"));
            Assert.True(ev.Idx.ContainsKey("age-n"));
            Assert.True(ev.Idx.ContainsKey("age1-n"));
            Assert.True(ev.Idx.ContainsKey("agedec-n"));
            Assert.True(ev.Idx.ContainsKey("agedec1-n"));
            Assert.True(ev.Idx.ContainsKey("agedbl-n"));
            Assert.True(ev.Idx.ContainsKey("agedbl1-n"));
            Assert.True(ev.Idx.ContainsKey("birthday-d"));
            Assert.True(ev.Idx.ContainsKey("birthdaywithoffset-d"));
        }

        [Fact]
        public async Task SyncStackTagsAsync() {
            await ResetAsync();

            const string Tag1 = "Tag One";
            const string Tag2 = "Tag Two";
            const string Tag2_Lowercase = "tag two";
            var client = IoC.GetInstance<IElasticClient>();

            PersistentEvent ev = EventData.GenerateEvent(projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, occurrenceDate: DateTime.Now);
            ev.Tags.Add(Tag1);

            var pipeline = IoC.GetInstance<EventPipeline>();
            await pipeline.RunAsync(ev);

            await client.RefreshAsync();
            ev = await _eventRepository.GetByIdAsync(ev.Id);
            Assert.NotNull(ev);
            Assert.NotNull(ev.StackId);

            var stack = await _stackRepository.GetByIdAsync(ev.StackId, true);
            Assert.Equal(new TagSet { Tag1 }, stack.Tags);

            ev = EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, occurrenceDate: DateTime.Now);
            ev.Tags.Add(Tag2);

            await pipeline.RunAsync(ev);
            stack = await _stackRepository.GetByIdAsync(ev.StackId, true);
            Assert.Equal(new TagSet { Tag1, Tag2 }, stack.Tags);

            ev = EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, generateTags: false, occurrenceDate: DateTime.Now);
            ev.Tags.Add(Tag2_Lowercase);

            await pipeline.RunAsync(ev);
            stack = await _stackRepository.GetByIdAsync(ev.StackId, true);
            Assert.Equal(new TagSet { Tag1, Tag2 }, stack.Tags);
        }

        [Fact]
        public async Task EnsureSingleNewStackAsync() {
            await ResetAsync();

            var pipeline = IoC.GetInstance<EventPipeline>();

            string source = Guid.NewGuid().ToString();
            var contexts = new List<EventContext> {
                new EventContext(new PersistentEvent { ProjectId = TestConstants.ProjectId, OrganizationId = TestConstants.OrganizationId, Message = "Test Sample", Source = source, Date = DateTime.UtcNow, Type = Event.KnownTypes.Log }),
                new EventContext(new PersistentEvent { ProjectId = TestConstants.ProjectId, OrganizationId = TestConstants.OrganizationId, Message = "Test Sample", Source = source, Date = DateTime.UtcNow, Type = Event.KnownTypes.Log}),
            };

            await pipeline.RunAsync(contexts);
            Assert.True(contexts.All(c => c.Stack.Id == contexts.First().Stack.Id));
            Assert.Equal(1, contexts.Count(c => c.IsNew));
            Assert.Equal(1, contexts.Count(c => !c.IsNew));
            Assert.Equal(2, contexts.Count(c => !c.IsRegression));
        }
        
        [Fact]
        public async Task EnsureSingleGlobalErrorStackAsync() {
            await ResetAsync();

            var pipeline = IoC.GetInstance<EventPipeline>();

            var contexts = new List<EventContext> {
                new EventContext(new PersistentEvent {
                    ProjectId = TestConstants.ProjectId,
                    OrganizationId = TestConstants.OrganizationId,
                    Message = "Test Exception",
                    Date = DateTime.UtcNow,
                    Type = Event.KnownTypes.Error,
                    Data = new DataDictionary { { "@error", new Error { Message = "Test Exception", Type = "Error" } } }
                }),
                new EventContext(new PersistentEvent {
                    ProjectId = TestConstants.ProjectId,
                    OrganizationId = TestConstants.OrganizationId,
                    Message = "Test Exception",
                    Date = DateTime.UtcNow,
                    Type = Event.KnownTypes.Error,
                    Data = new DataDictionary { { "@error", new Error { Message = "Test Exception 2", Type = "Error" } } }
                }),
            };

            await pipeline.RunAsync(contexts);
            Assert.True(contexts.All(c => c.Stack.Id == contexts.First().Stack.Id));
            Assert.Equal(1, contexts.Count(c => c.IsNew));
            Assert.Equal(1, contexts.Count(c => !c.IsNew));
            Assert.Equal(2, contexts.Count(c => !c.IsRegression));
        }

        [Fact]
        public async Task EnsureSingleRegressionAsync() {
            await ResetAsync();

            var pipeline = IoC.GetInstance<EventPipeline>();
            var client = IoC.GetInstance<IElasticClient>();

            PersistentEvent ev = EventData.GenerateEvent(projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow);
            var context = new EventContext(ev);
            await pipeline.RunAsync(context);
            Assert.True(context.IsProcessed);
            Assert.False(context.IsRegression);

            await client.RefreshAsync();
            ev = await _eventRepository.GetByIdAsync(ev.Id);
            Assert.NotNull(ev);

            var stack = await _stackRepository.GetByIdAsync(ev.StackId);
            stack.DateFixed = DateTime.UtcNow;
            stack.IsRegressed = false;
            await _stackRepository.SaveAsync(stack, true);

            var contexts = new List<EventContext> {
                new EventContext(EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow.AddMinutes(1))),
                new EventContext(EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow.AddMinutes(1)))
            };

            await pipeline.RunAsync(contexts);
            Assert.Equal(1, contexts.Count(c => c.IsRegression));
            Assert.Equal(1, contexts.Count(c => !c.IsRegression));

            contexts = new List<EventContext> {
                new EventContext(EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow.AddMinutes(1))),
                new EventContext(EventData.GenerateEvent(stackId: ev.StackId, projectId: TestConstants.ProjectId, organizationId: TestConstants.OrganizationId, occurrenceDate: DateTime.UtcNow.AddMinutes(1)))
            };

            await pipeline.RunAsync(contexts);
            Assert.Equal(2, contexts.Count(c => !c.IsRegression));
        }

        [Theory]
        [MemberData("Events")]
        public async Task ProcessEventsAsync(string errorFilePath) {
            await ResetAsync();

            var parserPluginManager = IoC.GetInstance<EventParserPluginManager>();
            var events = parserPluginManager.ParseEvents(File.ReadAllText(errorFilePath), 2, "exceptionless/2.0.0.0");
            Assert.NotNull(events);
            Assert.True(events.Count > 0);

            var pipeline = IoC.GetInstance<EventPipeline>();
            foreach (var ev in events) {
                ev.Date = DateTime.UtcNow;
                ev.ProjectId = TestConstants.ProjectId;
                ev.OrganizationId = TestConstants.OrganizationId;

                var context = new EventContext(ev);
                await  pipeline.RunAsync(context);
                Assert.True(context.IsProcessed);
            }
        }

        public static IEnumerable<object[]> Events {
            get {
                var result = new List<object[]>();
                foreach (var file in Directory.GetFiles(@"..\..\ErrorData\", "*.expected.json", SearchOption.AllDirectories))
                    result.Add(new object[] { file });

                return result.ToArray();
            }
        }
        
        private bool _isReset;
        private async Task ResetAsync() {
            if (!_isReset) {
                _isReset = true;
                await RemoveDataAsync(true);
                await CreateDataAsync();
            }
        }

        private async Task CreateDataAsync() {
            foreach (Organization organization in OrganizationData.GenerateSampleOrganizations()) {
                if (organization.Id == TestConstants.OrganizationId3)
                    BillingManager.ApplyBillingPlan(organization, BillingManager.FreePlan, UserData.GenerateSampleUser());
                else
                    BillingManager.ApplyBillingPlan(organization, BillingManager.SmallPlan, UserData.GenerateSampleUser());

                organization.StripeCustomerId = Guid.NewGuid().ToString("N");
                organization.CardLast4 = "1234";
                organization.SubscribeDate = DateTime.Now;

                if (organization.IsSuspended) {
                    organization.SuspendedByUserId = TestConstants.UserId;
                    organization.SuspensionCode = SuspensionCode.Billing;
                    organization.SuspensionDate = DateTime.Now;
                }

                await _organizationRepository.AddAsync(organization);
            }

            await _projectRepository.AddAsync(ProjectData.GenerateSampleProjects());

            foreach (User user in UserData.GenerateSampleUsers()) {
                if (user.Id == TestConstants.UserId) {
                    user.OrganizationIds.Add(TestConstants.OrganizationId2);
                    user.OrganizationIds.Add(TestConstants.OrganizationId3);
                }

                if (!user.IsEmailAddressVerified)
                    user.CreateVerifyEmailAddressToken();

                await _userRepository.AddAsync(user);
            }
        }

        private async Task RemoveDataAsync(bool removeUserAndProjectAndOrganizationData = false) {
            await _eventRepository.RemoveAllAsync();
            await _stackRepository.RemoveAllAsync();

            if (!removeUserAndProjectAndOrganizationData)
                return;

            await _tokenRepository.RemoveAllAsync();
            await _userRepository.RemoveAllAsync();
            await _projectRepository.RemoveAllAsync();
            await _organizationRepository.RemoveAllAsync();
        }

        public async void Dispose() {
            await RemoveDataAsync();
        }
    }
}