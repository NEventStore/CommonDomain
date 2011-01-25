namespace CommonDomain.Persistence.EventStore
{
	using System;
	using System.Collections;
	using System.Collections.Generic;
	using global::EventStore;
	using global::EventStore.Persistence;

	public class EventStoreRepository : IRepository
	{
		private const string AggregateTypeHeader = "AggregateType";
		private readonly IDictionary<Guid, int> commitSequence = new Dictionary<Guid, int>();
		private readonly IStoreEvents eventStore;
		private readonly IConstructAggregates factory;
		private readonly IStampAggregateVersion stamper;
		private readonly IDetectConflicts conflictDetector;

		public EventStoreRepository(
			IStoreEvents eventStore,
			IConstructAggregates factory,
			IStampAggregateVersion stamper,
			IDetectConflicts conflictDetector)
		{
			this.eventStore = eventStore;
			this.factory = factory;
			this.stamper = stamper;
			this.conflictDetector = conflictDetector;
		}

		public TAggregate GetById<TAggregate>(Guid id, int versionToLoad) where TAggregate : class, IAggregate
		{
			var stream = this.eventStore.ReadUntil(id, versionToLoad);
			this.commitSequence[id] = stream.CommitSequence;
			return this.BuildAggregate<TAggregate>(stream, versionToLoad);
		}
		private TAggregate BuildAggregate<TAggregate>(CommittedEventStream stream, int versionToLoad)
			where TAggregate : class, IAggregate
		{
			if (stream == null || stream.Events.Count == 0)
				return null;

			var aggregate = this.factory.Build(
				typeof(TAggregate), stream.StreamId, stream.Snapshot as IMemento);

			if (CanApplyEvents(aggregate, versionToLoad))
				foreach (var @event in stream.Events)
					aggregate.ApplyEvent(@event);

			return aggregate as TAggregate;
		}
		private static bool CanApplyEvents(IAggregate aggregate, int versionToLoad)
		{
			return versionToLoad == 0 || aggregate.Version < versionToLoad;
		}

		public void Save(IAggregate aggregate, Guid commitId, Action<IDictionary<string, object>> headers)
		{
			var attempt = this.BuildAttempt(aggregate, commitId);
			if (attempt.Events.Count == 0)
				return;

			if (headers != null)
				headers(attempt.Headers);

			attempt.Headers[AggregateTypeHeader] = aggregate.GetType().FullName;

			this.Persist(attempt);

			aggregate.ClearUncommittedEvents();
		}
		private CommitAttempt BuildAttempt(IAggregate aggregate, Guid commitId)
		{
			if (aggregate == null)
				throw new ArgumentNullException("aggregate", ExceptionMessages.NullArgument);

			var events = aggregate.GetUncommittedEvents();

			var attempt = new CommitAttempt
			{
				StreamId = aggregate.Id,
				StreamRevision = aggregate.Version,
				CommitId = commitId
			};

			int previousCommitSequence;
			if (this.commitSequence.TryGetValue(attempt.StreamId, out previousCommitSequence))
				attempt.PreviousCommitSequence = previousCommitSequence;

			foreach (var @event in events)
				attempt.Events.Add(new EventMessage { Body = @event });

			return attempt;
		}
		private void Persist(CommitAttempt attempt)
		{
			try
			{
				this.StampEventVersion(attempt);
				this.eventStore.Write(attempt);
				this.commitSequence[attempt.StreamId] = attempt.PreviousCommitSequence + 1;
			}
            catch (ConcurrencyException e)
            {
                var since = this.eventStore.ReadFrom(attempt.StreamId, attempt.StreamRevision);
                if (this.conflictDetector.ConflictsWith((ICollection)attempt.Events, (ICollection)since.Events))
                    throw new ConflictingCommandException(ExceptionMessages.ConflictingCommand, e);

                attempt.PreviousCommitSequence = since.CommitSequence;
                attempt.StreamRevision += since.Events.Count;

                this.commitSequence[attempt.StreamId] = ++attempt.PreviousCommitSequence;
            }
            //catch (ConcurrencyException e)
            //{
            //    var revisionBeforeAttempt = attempt.StreamRevision - attempt.Events.Count; //calculate my original revision

            //    //give me all commits for this stream that have incremented the revision beyond the one I started with, considering that the ReadFrom method includes the revision specified
            //    var since = this.eventStore.ReadFrom(attempt.StreamId, revisionBeforeAttempt + 1);

            //    if (this.conflictDetector.ConflictsWith((ICollection)attempt.Events, (ICollection)since.Events))
            //        throw new ConflictingCommandException(ExceptionMessages.ConflictingCommand, e);

            //    attempt.StreamRevision += since.Events.Count;
            //    attempt.PreviousCommitSequence = since.CommitSequence;

            //    this.Persist(attempt);
            //}
			catch (DuplicateCommitException)
			{
			}
			catch (PersistenceEngineException e)
			{
				throw new PersistenceException(e.Message, e);
			}
		}
		private void StampEventVersion(CommitAttempt attempt)
		{
			var version = attempt.StreamRevision - attempt.Events.Count + 1;
			this.stamper.SetVersion((ICollection)attempt.Events, version);
		}
	}
}