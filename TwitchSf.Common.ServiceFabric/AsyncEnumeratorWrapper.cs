using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Microsoft.ServiceFabric.Data;

namespace TwitchSf.Common.ServiceFabric
{
    internal struct AsyncEnumeratorWrapper<TSource> : IEnumerator<TSource>
    {
        private IAsyncEnumerator<TSource> source;

        public AsyncEnumeratorWrapper(IAsyncEnumerator<TSource> source)
        {
            this.source = source;
            this.Current = default(TSource);
        }

        public TSource Current { get; private set; }

        object IEnumerator.Current
        {
            get { throw new NotImplementedException(); }
        }

        public void Dispose()
        {
        }

        public bool MoveNext()
        {
            if (!this.source.MoveNextAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                return false;
            }

            this.Current = this.source.Current;
            return true;
        }

        public void Reset()
        {
            throw new NotImplementedException();
        }
    }
}