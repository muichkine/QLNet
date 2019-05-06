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
            for (int i = 0; i < leg.Count; ++i) {
                if (this [i].hasOccurred (settlementDate, includeSettlementDateFlows))
                    continue;

                double c = leg[i].amount ();
                if (this [i].tradingExCoupon (settlementDate)) {
                    c = 0.0;
                }

                t += getStepwiseDiscountTime (this [i], dc, npvDate, lastDate);
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

            for (int i = 0; i < leg.Count; ++i) {
                if (this[i].hasOccurred (settlementDate, includeSettlementDateFlows))
                    continue;

                double c = leg[i].amount ();
                if (this[i].tradingExCoupon (settlementDate)) {
                    c = 0.0;
                }

                t += getStepwiseDiscountTime (this[i], dc, npvDate, lastDate);

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
                lastDate = this[i].date ();
            }

            if (P.IsEqual (0.0)) // no cashflows
                return 0.0;
            return -dPdy / P; // reverse derivative sign
        }

      public double macaulayDuration(InterestRate y, bool includeSettlementDateFlows,
                                            Date settlementDate, Date npvDate)
      {
         Utils.QL_REQUIRE(y.compounding() == Compounding.Compounded, () => "compounded rate required");

         return (1.0 + y.rate() / (int)y.frequency()) *
                this.modifiedDuration(y, includeSettlementDateFlows, settlementDate, npvDate);
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
    }
}