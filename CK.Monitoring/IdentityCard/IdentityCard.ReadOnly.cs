using CK.Core;
using System;
using System.Collections.Generic;

namespace CK.Monitoring;

public sealed partial class IdentityCard
{
    /// <summary>
    /// Read only safe facade of a <see cref="IdentityCard"/> (the inner card is not exposed).
    /// </summary>
    public readonly struct ReadOnly
    {
        readonly IdentityCard _card;

        public ReadOnly( IdentityCard card )
        {
            Throw.CheckNotNullArgument( card );
            _card = card;
        }

        /// <inheritdoc cref="IdentityCard.OnChanged"/>
        public event Action<IdentiCardChangedEvent>? OnChanged
        {
            add { _card._onChange += value; }
            remove { _card._onChange -= value; }
        }

        /// <inheritdoc cref="IdentityCard.Identities"/>
        public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Identities => _card._exposed;

        /// <inheritdoc cref="IdentityCard.HasApplicationIdentity"/>
        public bool HasApplicationIdentity => _card._hasApplicationIdentity.IsCancellationRequested;

        /// <inheritdoc cref="IdentityCard.OnApplicationIdentityAvailable"/>
        public void OnApplicationIdentityAvailable( Action<IdentityCard> action ) => _card.OnApplicationIdentityAvailable( action );

        /// <inheritdoc cref="IdentityCard.ToString()"/>
        public override string ToString() => _card.ToString();
    }
}
