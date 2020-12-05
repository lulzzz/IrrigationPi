﻿using IrrigationApi.ApplicationCore.Configuration;
using IrrigationApi.ApplicationCore.Hardware;
using IrrigationApi.ApplicationCore.Threading;
using IrrigationApi.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace IrrigationApi.Backround
{
    public class IrrigationProcessorStatus
    {
        public bool Running { get; set; }
    }

    public class IrrigationProcessor : BackgroundService
    {
        private readonly ChannelReader<IrrigationJob> _jobReader;
        private readonly RelayBoard _relayBoard;
        private readonly IIrrigationStopper _irrigationStopper;
        private readonly IrrigationConfig _config;
        private readonly ILogger _logger;

        private CancellationTokenSource _irrigationCts;
        private readonly IrrigationProcessorStatus _status;

        public IrrigationProcessor(ChannelReader<IrrigationJob> jobReader,
                                    RelayBoard relayBoard,
                                    IIrrigationStopper irrigationStopper,
                                    IrrigationProcessorStatus status,
                                    IOptions<IrrigationConfig> config,
                                    ILogger<IrrigationProcessor> logger)
        {
            _jobReader = jobReader;
            _relayBoard = relayBoard;
            _irrigationStopper = irrigationStopper;
            _config = config.Value;
            _logger = logger;
            _status = status;

            _irrigationCts = new CancellationTokenSource();
            _irrigationStopper.StopRequested += StopIrrigation;
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _status.Running = true;

            while (await _jobReader.WaitToReadAsync(stoppingToken))
            {
                _irrigationCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                var executedJobs = new List<IrrigationJob>();

                //get the job that triggered this
                var job = await _jobReader.ReadAsync(_irrigationCts.Token);

                _logger.LogInformation("Incoming job: {@job}", job);

                //turn on the master control valve, then run the job for the desired duration
                _logger.LogInformation("Turning on MCV");
                _relayBoard[_config.MasterControlValveGpio].On = true;
                _logger.LogInformation("MCV on");

                await Irrigate(job, _irrigationCts.Token);
                executedJobs.Add(job);

                //before turning off the master control valve,
                //execute any jobs that were queued up while running the triggering job

                while (_jobReader.TryRead(out job))
                {
                    if (_irrigationCts.IsCancellationRequested)
                    { continue; } //just no-op this job so we can finish out this run. Cancellation was requested.

                    await Irrigate(job, _irrigationCts.Token);
                    executedJobs.Add(job);
                }


                //now that we've executed all of our irrigation jobs, 
                //turn off the master control valve
                //and then turn on our irrigated pins again for a short period of time
                //to relieve the pressure in the pipes and manifold

                _logger.LogInformation("Turning off MCV");
                _relayBoard[_config.MasterControlValveGpio].On = false;
                _logger.LogInformation("MCV off");


                _logger.LogInformation("Bleeding pressure on {@executedJobs}", executedJobs);
                var pins = executedJobs.Select(j => j.Valve)
                                       .Select(v => _config.Valves.First(cv => cv.ValveNumber == v).GpioPin)
                                       .Distinct();

                foreach (var irrigatedPin in pins)
                {
                    _relayBoard[irrigatedPin].On = true;
                }

                //wait a bit for the pressure to bleed out
                await Task.Delay(_config.PressureBleedTime);

                //then close the valves
                foreach (var irrigatedPin in pins)
                {
                    _relayBoard[irrigatedPin].On = false;
                }

                _logger.LogInformation("Pressure bled");
                _logger.LogInformation("Irrigation complete");
            }

            _status.Running = false;
        }

        private async Task Irrigate(IrrigationJob job, CancellationToken cancellationToken)
        {
            var valvePin = _config.Valves.First(v => v.ValveNumber == job.Valve).GpioPin;

            _relayBoard[valvePin].On = true;

            try
            { await Task.Delay(job.Duration, cancellationToken); }
            catch { }

            _relayBoard[valvePin].On = true;
        }

        private void StopIrrigation(object sender, EventArgs e)
        {
            _irrigationCts.Cancel();
        }

    }
}
