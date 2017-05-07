using System.Collections;
using System.Collections.Generic;
using Microsoft.ServiceFabric.Data;

namespace TwitchSf.Common.ServiceFabric
{
    internal struct AsyncEnumerableWrapper<TSource> : IEnumerable<TSource>
    {
        private IAsyncEnumerable<TSource> source;

        public AsyncEnumerableWrapper(IAsyncEnumerable<TSource> source)
        {
            this.source = source;
        }

        public IEnumerator<TSource> GetEnumerator()
        {
            return new AsyncEnumeratorWrapper<TSource>(this.source.GetAsyncEnumerator());
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
}