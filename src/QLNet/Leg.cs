using System.Collections.Generic;

namespace QLNet {
    public class Leg : IList<CashFlow> {
        protected List<CashFlow> cashflows_;

        public Leg () {
            cashflows_ = new List<CashFlow> ();
        }

        public Leg (IEnumerable<CashFlow> leg) {
            cashflows_ = new List<CashFlow> (leg);
        }

        public Leg (int capacity) {
            cashflows_ = new List<CashFlow> (capacity);
        }

        #region IList and List-style interface

        public int Count {
            get {
                return cashflows_.Count;
            }
        }

        public void Append (CashFlow cf) {
            cashflows_.Add (cf);
        }

        public void Add (CashFlow cf) {
            this.Append (cf);
        }

        public void Prepend (CashFlow cf) {
            this.Insert (0, cf);
        }

        public CashFlow this [int index] {
            get {
                return cashflows_[index];
            }
            set {
                cashflows_[index] = value;
            }
        }

        public void Clear () {
            cashflows_.Clear ();
        }

        public bool Contains (CashFlow cf) {
            return cashflows_.Contains (cf);
        }

        public int IndexOf (CashFlow cf) {
            return cashflows_.IndexOf (cf);
        }

        public IEnumerator<CashFlow> GetEnumerator () {
            return cashflows_.GetEnumerator ();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator () {
            return this.GetEnumerator ();
        }

        public CashFlow First () {
            return cashflows_[0];
        }

        public CashFlow Last () {
            if (this.Count == 0) return null;
            return cashflows_[this.Count - 1];
        }

        public void Insert (int index, CashFlow cf) {
            cashflows_.Insert (index, cf);
        }

        public bool Remove (CashFlow cf) {
            return cashflows_.Remove (cf);
        }

        public void RemoveAt (int index) {
            cashflows_.RemoveAt (index);
        }

        public void RemoveRange (int index, int count) {
            cashflows_.RemoveRange (index, count);
        }

        public void Sort () {
            cashflows_.Sort ();
        }

        public void Sort (int index, int count, System.Collections.Generic.IComparer<CashFlow> comparer) {
            cashflows_.Sort (index, count, comparer);
        }

        public void CopyTo (CashFlow[] array, int index) {
            cashflows_.CopyTo (array, index);
        }

        public bool IsFixedSize {
            get {
                return false;
            }
        }

        public bool IsReadOnly {
            get {
                return false;
            }
        }

        #endregion

        #region Util functions

        public double simpleDuration (InterestRate y, bool includeSettlementDateFlows,
            Date settlementDate, Date npvDate) {
            if (this.empty ())
                return 0.0;

            if (settlementDate == null)
                settlementDate = Settings.evaluationDate ();

            if (npvDate == null)
                npvDate = settlementDate;

            double P = 0.0;
            double dPdy = 0.0;
            double t = 0.0;
            Date lastDate = npvDate;

            DayCounter dc = y.dayCounter ();
            for (int i = 0; i < this.Count; ++i) {
                if (this [i].hasOccurred (settlementDate, includeSettlementDateFlows))
                    continue;

                double c = this [i].amount ();
                if (this [i].tradingExCoupon (settlementDate)) {
                    c = 0.0;
                }

                t += CashFlows.getStepwiseDiscountTime (this [i], dc, npvDate, lastDate);
                double B = y.discountFactor (t);
                P += c * B;
                dPdy += t * c * B;

                lastDate = this [i].date ();
            }

            if (P.IsEqual (0.0)) // no cashflows
                return 0.0;
            return dPdy / P;
        }

        public double modifiedDuration (InterestRate y, bool includeSettlementDateFlows,
            Date settlementDate, Date npvDate) {
            if (this.empty ())
                return 0.0;

            if (settlementDate == null)
                settlementDate = Settings.evaluationDate ();

            if (npvDate == null)
                npvDate = settlementDate;

            double P = 0.0;
            double t = 0.0;
            double dPdy = 0.0;
            double r = y.rate ();
            int N = (int) y.frequency ();
            Date lastDate = npvDate;
            DayCounter dc = y.dayCounter ();

            for (int i = 0; i < this.Count; ++i) {
                if (this [i].hasOccurred (settlementDate, includeSettlementDateFlows))
                    continue;

                double c = this [i].amount ();
                if (this [i].tradingExCoupon (settlementDate)) {
                    c = 0.0;
                }

                t += CashFlows.getStepwiseDiscountTime (this [i], dc, npvDate, lastDate);

                double B = y.discountFactor (t);
                P += c * B;
                switch (y.compounding ()) {
                    case Compounding.Simple:
                        dPdy -= c * B * B * t;
                        break;
                    case Compounding.Compounded:
                        dPdy -= c * t * B / (1 + r / N);
                        break;
                    case Compounding.Continuous:
                        dPdy -= c * B * t;
                        break;
                    case Compounding.SimpleThenCompounded:
                        if (t <= 1.0 / N)
                            dPdy -= c * B * B * t;
                        else
                            dPdy -= c * t * B / (1 + r / N);
                        break;
                    default:
                        Utils.QL_FAIL ("unknown compounding convention (" + y.compounding () + ")");
                        break;
                }
                lastDate = this [i].date ();
            }

            if (P.IsEqual (0.0)) // no cashflows
                return 0.0;
            return -dPdy / P; // reverse derivative sign
        }

        public double macaulayDuration (InterestRate y, bool includeSettlementDateFlows,
            Date settlementDate, Date npvDate) {
            Utils.QL_REQUIRE (y.compounding () == Compounding.Compounded, () => "compounded rate required");

            return (1.0 + y.rate () / (int) y.frequency ()) *
                this.modifiedDuration (y, includeSettlementDateFlows, settlementDate, npvDate);
        }

        private double aggregateRate (CashFlow cf) {
            if (cf == null)
                return 0.0;

            Date paymentDate = cf.date ();
            bool firstCouponFound = false;
            double nominal = 0.0;
            double accrualPeriod = 0.0;
            DayCounter dc = null;
            double result = 0.0;

            foreach (CashFlow x in this.cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null) {
                    if (firstCouponFound) {
                        Utils.QL_REQUIRE (nominal.IsEqual (cp.nominal ()) &&
                            accrualPeriod.IsEqual (cp.accrualPeriod ()) &&
                            dc == cp.dayCounter (), () =>
                            "cannot aggregate two different coupons on " +
                            paymentDate);
                    } else {
                        firstCouponFound = true;
                        nominal = cp.nominal ();
                        accrualPeriod = cp.accrualPeriod ();
                        dc = cp.dayCounter ();
                    }
                    result += cp.rate ();
                }
            }

            Utils.QL_REQUIRE (firstCouponFound, () => "no coupon paid at cashflow date " + paymentDate);
            return result;
        }

        #endregion

        //! Cash-flow duration.
        public double duration (InterestRate rate, Duration.Type type, bool includeSettlementDateFlows,
            Date settlementDate = null, Date npvDate = null) {
            if (this.empty ())
                return 0.0;

            if (settlementDate == null)
                settlementDate = Settings.evaluationDate ();

            if (npvDate == null)
                npvDate = settlementDate;

            switch (type) {
                case Duration.Type.Simple:
                    return this.simpleDuration (rate, includeSettlementDateFlows, settlementDate, npvDate);
                case Duration.Type.Modified:
                    return this.modifiedDuration (rate, includeSettlementDateFlows, settlementDate, npvDate);
                case Duration.Type.Macaulay:
                    return this.macaulayDuration (rate, includeSettlementDateFlows, settlementDate, npvDate);
                default:
                    Utils.QL_FAIL ("unknown duration type");
                    break;
            }
            return 0.0;
        }

        public double duration (double yield, DayCounter dayCounter, Compounding compounding, Frequency frequency,
            Duration.Type type, bool includeSettlementDateFlows, Date settlementDate = null,
            Date npvDate = null) {
            return this.duration (new InterestRate (yield, dayCounter, compounding, frequency),
                type, includeSettlementDateFlows, settlementDate, npvDate);
        }

        //! Implied internal rate of return.
        // The function verifies
        // the theoretical existance of an IRR and numerically
        // establishes the IRR to the desired precision.
        public double yield (double npv, DayCounter dayCounter, Compounding compounding, Frequency frequency,
            bool includeSettlementDateFlows, Date settlementDate = null, Date npvDate = null,
            double accuracy = 1.0e-10, int maxIterations = 100, double guess = 0.05) {
            NewtonSafe solver = new NewtonSafe ();
            solver.setMaxEvaluations (maxIterations);
            Cashflows.IrrFinder objFunction = new IrrFinder (this, npv,
                dayCounter, compounding, frequency,
                includeSettlementDateFlows,
                settlementDate, npvDate);
            return solver.solve (objFunction, accuracy, guess, guess / 10.0);
        }

        #region Date functions

        public Date startDate () {
            Utils.QL_REQUIRE (!this.empty (), () => "empty leg");
            Date d = Date.maxDate ();
            for (int i = 0; i < this.Count; ++i) {
                Coupon c = this [i] as Coupon;
                if (c != null)
                    d = Date.Min (d, c.accrualStartDate ());
                else
                    d = Date.Min (d, this [i].date ());
            }
            return d;
        }
        public Date maturityDate () {
            Utils.QL_REQUIRE (!this.empty (), () => "empty leg");
            Date d = Date.minDate ();
            for (int i = 0; i < this.Count; ++i) {
                Coupon c = this [i] as Coupon;
                if (c != null)
                    d = Date.Max (d, c.accrualEndDate ());
                else
                    d = Date.Max (d, this [i].date ());
            }
            return d;
        }
        public bool isExpired (bool includeSettlementDateFlows, Date settlementDate = null) {
            if (this.empty ())
                return true;

            if (settlementDate == null)
                settlementDate = Settings.evaluationDate ();

            for (int i = this.Count; i > 0; --i)
                if (!this [i - 1].hasOccurred (settlementDate, includeSettlementDateFlows))
                    return false;
            return true;
        }
        #endregion

        #region CashFlow functions
        //! the last cashflow paying before or at the given date
        public CashFlow previousCashFlow (bool includeSettlementDateFlows, Date settlementDate = null) {
            if (this.empty ())
                return null;

            Date d = (settlementDate ?? Settings.evaluationDate ());
            return cashflows_.LastOrDefault (x => x.hasOccurred (d, includeSettlementDateFlows));
        }
        //! the first cashflow paying after the given date
        public CashFlow nextCashFlow (bool includeSettlementDateFlows, Date settlementDate = null) {
            if (this.empty ())
                return null;

            Date d = (settlementDate ?? Settings.evaluationDate ());

            // the first coupon paying after d is the one we're after
            return cashflows_.FirstOrDefault (x => !x.hasOccurred (d, includeSettlementDateFlows));
        }
        public Date previousCashFlowDate (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = cashflows_.previousCashFlow (includeSettlementDateFlows, settlementDate);

            if (cf == null)
                return null;

            return cf.date ();
        }
        public Date nextCashFlowDate (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);

            if (cf == null)
                return null;

            return cf.date ();
        }
        public double? previousCashFlowAmount (bool includeSettlementDateFlows, Date settlementDate = null) {

            CashFlow cf = this.previousCashFlow (includeSettlementDateFlows, settlementDate);

            if (cf == null)
                return null;

            Date paymentDate = cf.date ();
            double? result = 0.0;
            result = cashflows_.Where (cf1 => cf1.date () == paymentDate).Sum (cf1 => cf1.amount ());
            return result;
        }
        public double? nextCashFlowAmount (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);

            if (cf == null)
                return null;

            Date paymentDate = cf.date ();
            double result = 0.0;
            result = cashflows_.Where (cf1 => cf1.date () == paymentDate).Sum (cf1 => cf1.amount ());
            return result;
        }
        #endregion

        #region Coupon inspectors

        public double previousCouponRate (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.previousCashFlow (includeSettlementDateFlows, settlementDate);
            return this.aggregateRate (cf);
        }
        public double nextCouponRate (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            return this.aggregateRate (cf);
        }
        public double nominal (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return 0.0;

            Date paymentDate = cf.date ();

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.nominal ();
            }
            return 0.0;
        }
        public Date accrualStartDate (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return null;

            Date paymentDate = cf.date ();

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.accrualStartDate ();
            }
            return null;
        }
        public Date accrualEndDate (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return null;

            Date paymentDate = cf.date ();

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.accrualEndDate ();
            }
            return null;
        }
        public Date referencePeriodStart (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return null;
            Date paymentDate = cf.date ();

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.referencePeriodStart;
            }
            return null;
        }
        public Date referencePeriodEnd (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return null;
            Date paymentDate = cf.date ();

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.referencePeriodEnd;
            }
            return null;
        }
        public double accrualPeriod (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return 0;
            Date paymentDate = cf.date ();

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.accrualPeriod ();
            }
            return 0;
        }
        public int accrualDays (bool includeSettlementDateFlows, Date settlementDate = null) {
            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return 0;
            Date paymentDate = cf.date ();

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.accrualDays ();
            }
            return 0;
        }
        public double accruedPeriod (bool includeSettlementDateFlows, Date settlementDate = null) {
            if (settlementDate == null)
                settlementDate = Settings.evaluationDate ();

            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return 0;

            Date paymentDate = cf.date ();
            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.accruedPeriod (settlementDate);
            }
            return 0;
        }
        public int accruedDays (bool includeSettlementDateFlows, Date settlementDate = null) {
            if (settlementDate == null)
                settlementDate = Settings.evaluationDate ();

            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return 0;
            Date paymentDate = cf.date ();

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    return cp.accruedDays (settlementDate);
            }
            return 0;
        }
        public double accruedAmount (bool includeSettlementDateFlows, Date settlementDate = null) {
            if (settlementDate == null)
                settlementDate = Settings.evaluationDate ();

            CashFlow cf = this.nextCashFlow (includeSettlementDateFlows, settlementDate);
            if (cf == null)
                return 0;

            Date paymentDate = cf.date ();
            double result = 0.0;

            foreach (CashFlow x in cashflows_.Where (x => x.date () == paymentDate)) {
                Coupon cp = x as Coupon;
                if (cp != null)
                    result += cp.accruedAmount (settlementDate);
            }
            return result;
        }
        #endregion
    }
}