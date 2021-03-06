﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Exceptionless.Core.Extensions;
using Exceptionless.Core.Messaging.Models;
using Exceptionless.Core.Models;
using Exceptionless.Core.Repositories.Configuration;
using FluentValidation;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Messaging;
using Nest;

namespace Exceptionless.Core.Repositories {
    public class StackRepository : ElasticSearchRepositoryOwnedByOrganizationAndProject<Stack>, IStackRepository {
        private const string STACKING_VERSION = "v2";
        private readonly IEventRepository _eventRepository;

        public StackRepository(IElasticClient elasticClient, StackIndex index, IEventRepository eventRepository, IValidator<Stack> validator = null, ICacheClient cacheClient = null, IMessagePublisher messagePublisher = null)
            : base(elasticClient, index, validator, cacheClient, messagePublisher) {
            _eventRepository = eventRepository;
            DocumentChanging += OnDocumentChangingAsync;
        }

        private async Task OnDocumentChangingAsync(object sender, DocumentChangeEventArgs<Stack> args) {
            if (args.ChangeType != ChangeType.Removed)
                return;

            foreach (Stack document in args.Documents) {
                if (await _eventRepository.GetCountByStackIdAsync(document.Id).AnyContext() > 0)
                    throw new ApplicationException($"Stack \"{document.Id}\" can't be deleted because it has events associated to it.");
            }
        }

        protected override async Task AddToCacheAsync(ICollection<Stack> documents, TimeSpan? expiresIn = null) {
            await base.AddToCacheAsync(documents, expiresIn).AnyContext();
            foreach (var stack in documents)
                await Cache.SetAsync(GetScopedCacheKey(GetStackSignatureCacheKey(stack)), stack, expiresIn ?? TimeSpan.FromSeconds(RepositoryConstants.DEFAULT_CACHE_EXPIRATION_SECONDS)).AnyContext();
        }

        private string GetStackSignatureCacheKey(Stack stack) {
            return GetStackSignatureCacheKey(stack.ProjectId, stack.SignatureHash);
        }

        private string GetStackSignatureCacheKey(string projectId, string signatureHash) {
            return String.Concat(projectId, "-", signatureHash, "-", STACKING_VERSION);
        }

        public async Task IncrementEventCounterAsync(string organizationId, string projectId, string stackId, DateTime minOccurrenceDateUtc, DateTime maxOccurrenceDateUtc, int count, bool sendNotifications = true) {
            // If total occurrences are zero (stack data was reset), then set first occurrence date
            // Only update the LastOccurrence if the new date is greater then the existing date.
            var result = await _elasticClient.UpdateAsync<Stack>(s => s
                .Id(stackId)
                .RetryOnConflict(3)
                .Lang("groovy")
                .Script(@"if (ctx._source.total_occurrences == 0 || ctx._source.first_occurrence > minOccurrenceDateUtc) {
                            ctx._source.first_occurrence = minOccurrenceDateUtc;
                          }
                          if (ctx._source.last_occurrence < maxOccurrenceDateUtc) {
                            ctx._source.last_occurrence = maxOccurrenceDateUtc;
                          }
                          ctx._source.total_occurrences += count;")
                .Params(p => p
                    .Add("minOccurrenceDateUtc", minOccurrenceDateUtc)
                    .Add("maxOccurrenceDateUtc", maxOccurrenceDateUtc)
                    .Add("count", count))).AnyContext();
            
            if (!result.IsValid) {
                Logger.Error().Message("Error occurred incrementing total event occurrences on stack \"{0}\". Error: {1}", stackId, result.ServerError.Error).Write();
                return;
            }

            await InvalidateCacheAsync(stackId).AnyContext();

            if (sendNotifications) {
                await PublishMessageAsync(new EntityChanged {
                    ChangeType = ChangeType.Saved,
                    Id = stackId,
                    OrganizationId = organizationId,
                    ProjectId = projectId,
                    Type = _entityType
                }, TimeSpan.FromSeconds(1.5)).AnyContext();
            }
        }

        public Task<Stack> GetStackBySignatureHashAsync(string projectId, string signatureHash) {
            return FindOneAsync(new ElasticSearchOptions<Stack>()
                .WithProjectId(projectId)
                .WithFilter(Filter<Stack>.Term(s => s.SignatureHash, signatureHash))
                .WithCacheKey(GetStackSignatureCacheKey(projectId, signatureHash)));
        }

        public Task<FindResults<Stack>> GetByFilterAsync(string systemFilter, string userFilter, string sort, SortOrder sortOrder, string field, DateTime utcStart, DateTime utcEnd, PagingOptions paging) {
            if (String.IsNullOrEmpty(sort)) {
                sort = "last";
                sortOrder = SortOrder.Descending;
            }

            var search = new ElasticSearchOptions<Stack>()
                .WithDateRange(utcStart, utcEnd, field ?? "last")
                .WithFilter(!String.IsNullOrEmpty(systemFilter) ? Filter<Stack>.Query(q => q.QueryString(qs => qs.DefaultOperator(Operator.And).Query(systemFilter))) : null)
                .WithQuery(userFilter)
                .WithPaging(paging)
                .WithSort(e => e.OnField(sort).Order(sortOrder == SortOrder.Descending ? Nest.SortOrder.Descending : Nest.SortOrder.Ascending));

            return FindAsync(search);
        }

        public Task<FindResults<Stack>> GetMostRecentAsync(string projectId, DateTime utcStart, DateTime utcEnd, PagingOptions paging, string query) {
            var options = new ElasticSearchOptions<Stack>().WithProjectId(projectId).WithQuery(query).WithSort(s => s.OnField(e => e.LastOccurrence).Descending()).WithPaging(paging);
            options.Filter = Filter<Stack>.Range(r => r.OnField(s => s.LastOccurrence).GreaterOrEquals(utcStart));
            options.Filter &= Filter<Stack>.Range(r => r.OnField(s => s.LastOccurrence).LowerOrEquals(utcEnd));

            return FindAsync(options);
        }

        public Task<FindResults<Stack>> GetNewAsync(string projectId, DateTime utcStart, DateTime utcEnd, PagingOptions paging, string query) {
            var options = new ElasticSearchOptions<Stack>().WithProjectId(projectId).WithQuery(query).WithSort(s => s.OnField(e => e.FirstOccurrence).Descending()).WithPaging(paging);
            options.Filter = Filter<Stack>.Range(r => r.OnField(s => s.FirstOccurrence).GreaterOrEquals(utcStart));
            options.Filter &= Filter<Stack>.Range(r => r.OnField(s => s.FirstOccurrence).LowerOrEquals(utcEnd));

            return FindAsync(options);
        }

        public async Task MarkAsRegressedAsync(string stackId) {
            var stack = await GetByIdAsync(stackId).AnyContext();
            stack.DateFixed = null;
            stack.IsRegressed = true;
            await SaveAsync(stack, true).AnyContext();
        }

        protected override async Task InvalidateCacheAsync(ICollection<Stack> stacks, ICollection<Stack> originalStacks) {
            if (!EnableCache)
                return;

            foreach (var stack in stacks)
                await InvalidateCacheAsync(GetStackSignatureCacheKey(stack)).AnyContext();

            await base.InvalidateCacheAsync(stacks, originalStacks).AnyContext();
        }

        public async Task InvalidateCacheAsync(string projectId, string stackId, string signatureHash) {
            await InvalidateCacheAsync(stackId).AnyContext();
            await InvalidateCacheAsync(GetStackSignatureCacheKey(projectId, signatureHash)).AnyContext();
        }
    }
}