"""Sample DAGs for the AEGIS dev stack, so the Airflow collector has something to collect.

    aegis_sample_cigna_eligibility_load   succeeds every 2 minutes; owner declared on the DAG
    aegis_sample_humana_claims_load       fails every 3 minutes; owner left at Airflow's default
                                          (which AEGIS treats as unowned, so it lands on the gap list)
    aegis_sample_legacy_uhc_extract       paused; shows up as an inactive job

Everything here is invented. No real feed, carrier file, or path.
"""

from datetime import datetime

from airflow import DAG
from airflow.operators.bash import BashOperator
from airflow.operators.python import PythonOperator

START = datetime(2026, 9, 1)


def _vendor_drop_missing():
    raise FileNotFoundError("Could not find file '/landing/HUMANA_CLAIMS_20260903.csv'. The vendor drop did not arrive.")


with DAG(
    dag_id="aegis_sample_cigna_eligibility_load",
    description="Owner: ETL; Ticket: DE-202. Loads the Cigna eligibility feed into staging.",
    schedule="*/2 * * * *",
    start_date=START,
    catchup=False,
    is_paused_upon_creation=False,
    tags=["aegis", "team:etl"],
    default_args={"owner": "etl", "retries": 0},
) as cigna_eligibility:
    BashOperator(task_id="load_staging", bash_command="sleep 20 && echo loaded")


with DAG(
    dag_id="aegis_sample_humana_claims_load",
    description="Loads the Humana claims feed. See wiki.",
    schedule="*/3 * * * *",
    start_date=START,
    catchup=False,
    is_paused_upon_creation=False,
    tags=["aegis"],
    default_args={"owner": "airflow", "retries": 0},
) as humana_claims:
    PythonOperator(task_id="validate_drop", python_callable=_vendor_drop_missing)


with DAG(
    dag_id="aegis_sample_legacy_uhc_extract",
    description="Legacy UHC extract, kept for reference. #carrier",
    schedule="@daily",
    start_date=START,
    catchup=False,
    is_paused_upon_creation=True,
    tags=["aegis", "legacy"],
    default_args={"owner": "carrier-integration", "retries": 0},
) as legacy_uhc:
    BashOperator(task_id="noop", bash_command="true")
