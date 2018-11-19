﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ESFA.DC.EAS1819.DataService.Interface;
using ESFA.DC.EAS1819.EF;
using ESFA.DC.EAS1819.Interface;
using ESFA.DC.EAS1819.Interface.FileData;
using ESFA.DC.EAS1819.Interface.Validation;
using ESFA.DC.EAS1819.Model;
using ESFA.DC.EAS1819.Service.Helpers;
using ESFA.DC.JobContextManager.Model.Interface;
using ESFA.DC.Logging.Interfaces;

namespace ESFA.DC.EAS1819.Service.Tasks
{
    public class StorageTask : IEasServiceTask
    {
        private readonly IEasSubmissionService _easSubmissionService;
        private readonly IEasPaymentService _easPaymentService;
        private readonly IValidationService _validationService;
        private readonly IFileDataCacheService _fileDataCacheService;
        private readonly ILogger _logger;

        public StorageTask(
            IEasSubmissionService easSubmissionService,
            IEasPaymentService easPaymentService,
            IValidationService validationService,
            IFileDataCacheService fileDataCacheService,
            ILogger logger)
        {
            _easSubmissionService = easSubmissionService;
            _easPaymentService = easPaymentService;
            _validationService = validationService;
            _fileDataCacheService = fileDataCacheService;
            _logger = logger;
        }

        public string TaskName => "Storage";

        public async Task ExecuteAsync(IJobContextMessage jobContextMessage, EasFileInfo fileInfo, CancellationToken cancellationToken)
        {
            _logger.LogInfo("Storage Task is called.");

            try
            {
                var fileDataCache = await _fileDataCacheService.GetFileDataCacheAsync(fileInfo.UKPRN, cancellationToken);
                if (fileDataCache != null && !fileDataCache.FailedFileValidation && (fileDataCache.ValidEasCsvRecords?.Count > 0 || fileDataCache.ValidationErrors?.Count > 0))
                {
                    var paymentTypes = _easPaymentService.GetAllPaymentTypes();
                    var submissionId = Guid.NewGuid();
                    var submissionList = BuildSubmissionList(fileInfo, fileDataCache.ValidEasCsvRecords, submissionId);
                    var submissionValuesList = BuildEasSubmissionValues(fileDataCache.ValidEasCsvRecords, paymentTypes, submissionId);
                    await _easSubmissionService.PersistEasSubmissionAsync(submissionList, submissionValuesList, cancellationToken);
                    await _validationService.LogValidationErrorsAsync(fileDataCache.ValidationErrors, fileInfo, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Storage Task failed", ex);
                throw;
            }
        }

        private static List<EasSubmission> BuildSubmissionList(EasFileInfo fileInfo, IList<EasCsvRecord> easCsvRecords, Guid submissionId)
        {
            List<int> distinctCollectionPeriods = new List<int>();
            var submissionList = new List<EasSubmission>();
            foreach (var record in easCsvRecords)
            {
                distinctCollectionPeriods.Add(
                    CollectionPeriodHelper.GetCollectionPeriod(record.CalendarYear, record.CalendarMonth));
            }

            distinctCollectionPeriods = distinctCollectionPeriods.Distinct().ToList();

            var list = easCsvRecords.Select(p => new
            {
                collectionPeriod = CollectionPeriodHelper.GetCollectionPeriod(p.CalendarYear, p.CalendarMonth)
            }).Distinct().ToList();

            foreach (var collectionPeriod in distinctCollectionPeriods)
            {
                var easSubmission = new EasSubmission()
                {
                    SubmissionId = submissionId,
                    CollectionPeriod = collectionPeriod,
                    DeclarationChecked = true,
                    NilReturn = false,
                    ProviderName = string.Empty,
                    Ukprn = fileInfo.UKPRN,
                    UpdatedOn = DateTime.Now,
                };
                submissionList.Add(easSubmission);
            }

            return submissionList;
        }

        private static List<EasSubmissionValues> BuildEasSubmissionValues(IList<EasCsvRecord> easCsvRecords, List<PaymentTypes> paymentTypes, Guid submissionId)
        {
            var submissionValuesList = new List<EasSubmissionValues>();
            foreach (var easRecord in easCsvRecords)
            {
                var paymentType = paymentTypes.FirstOrDefault(x => x.FundingLine?.Name == easRecord.FundingLine
                                                                   && x.AdjustmentType?.Name == easRecord.AdjustmentType);
                if (paymentType is null)
                {
                    throw new Exception(
                        $"Funding Line : {easRecord.FundingLine} , AdjustmentType combination :  {easRecord.AdjustmentType}  does not exist.");
                }

                var easSubmissionValues = new EasSubmissionValues()
                {
                    PaymentId = paymentType.PaymentId,
                    CollectionPeriod =
                        CollectionPeriodHelper.GetCollectionPeriod(easRecord.CalendarYear, easRecord.CalendarMonth),
                    PaymentValue = easRecord.Value.GetValueOrDefault(),
                    SubmissionId = submissionId,
                };
                submissionValuesList.Add(easSubmissionValues);
            }

            return submissionValuesList;
        }
    }
}
