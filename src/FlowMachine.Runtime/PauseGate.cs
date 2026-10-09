using System;
using System.Threading;
using System.Threading.Tasks;

namespace FlowMachine.Runtime
{
    public sealed class PauseGate
    {
        private readonly object _sync = new object();
        private TaskCompletionSource<bool> _resumeSource;
        private TaskCompletionSource<bool> _pauseReachedSource;
        private readonly TaskCompletionSource<bool> _completedSource;
        private bool _pauseRequested;
        private bool _cancelled;

        public PauseGate()
        {
            _completedSource = NewSource();
        }

        public void RequestPause()
        {
            lock (_sync)
            {
                if (!_cancelled && !_completedSource.Task.IsCompleted)
                {
                    _pauseRequested = true;
                    if (_pauseReachedSource == null)
                    {
                        _pauseReachedSource = NewSource();
                    }
                }
            }
        }

        public void Resume()
        {
            TaskCompletionSource<bool> resume;
            lock (_sync)
            {
                _pauseRequested = false;
                resume = _resumeSource;
                _resumeSource = null;
                _pauseReachedSource = null;
            }

            if (resume != null)
            {
                resume.TrySetResult(true);
            }
        }

        public void Cancel()
        {
            TaskCompletionSource<bool> resume;
            lock (_sync)
            {
                _cancelled = true;
                _pauseRequested = false;
                resume = _resumeSource;
                _resumeSource = null;
                if (_pauseReachedSource != null)
                {
                    _pauseReachedSource.TrySetResult(false);
                }
            }

            if (resume != null)
            {
                resume.TrySetResult(false);
            }
        }

        public async Task<bool> WaitIfPauseRequestedAsync(Action onPaused, Action onResumed,
            CancellationToken cancellationToken)
        {
            Task resumeTask;
            lock (_sync)
            {
                if (!_pauseRequested || _cancelled)
                {
                    return false;
                }

                _resumeSource = NewSource();
                if (_pauseReachedSource == null)
                {
                    _pauseReachedSource = NewSource();
                }

                resumeTask = _resumeSource.Task;
            }

            onPaused();
            lock (_sync)
            {
                _pauseReachedSource.TrySetResult(true);
            }

            await resumeTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            onResumed();
            return true;
        }

        public async Task<bool> WaitForPauseOrCompletionAsync()
        {
            Task<bool> pauseTask;
            lock (_sync)
            {
                if (_pauseReachedSource == null)
                {
                    _pauseReachedSource = NewSource();
                }

                pauseTask = _pauseReachedSource.Task;
            }

            Task<bool> completed = await Task.WhenAny(pauseTask, _completedSource.Task)
                .ConfigureAwait(false);
            return await completed.ConfigureAwait(false);
        }

        public void Complete()
        {
            lock (_sync)
            {
                _completedSource.TrySetResult(false);
                if (_pauseReachedSource != null)
                {
                    _pauseReachedSource.TrySetResult(false);
                }
            }
        }

        private static TaskCompletionSource<bool> NewSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
