defmodule Octocon.Auth.Guardian do
  @moduledoc """
  Guardian configuration for Octocon.

  This module is used by `Guardian.Plug` plugs to authenticate and authorize
  requests, and provides helper utilities for generating and validating JWTs.
  """

  use Guardian, otp_app: :octocon

  def subject_for_token(id, claims)

  @spec subject_for_token(any, any) :: {:error, :reason_for_error} | {:ok, binary}
  def subject_for_token(id, _claims) when is_binary(id) do
    sub = to_string(id)
    {:ok, sub}
  end

  def subject_for_token(_, _) do
    {:error, :no_id_in_claims}
  end

  def resource_from_claims(%{"sub" => id}) do
    {:ok, id}
  end

  def resource_from_claims(_claims) do
    {:error, :no_id_in_claims}
  end
end
