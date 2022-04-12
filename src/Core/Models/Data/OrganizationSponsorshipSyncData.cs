﻿using System;
using System.Collections.Generic;

namespace Bit.Core.Models.Data
{
    public class OrganizationSponsorshipSyncData
    {
        public string BillingSyncKey { get; set; }
        public Guid SponsoringOrganizationCloudId { get; set; }
        public IEnumerable<OrganizationSponsorshipData> SponsorshipsBatch { get; set; }
    }
}